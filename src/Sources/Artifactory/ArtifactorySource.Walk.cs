using System.Collections.Concurrent;
using System.Globalization;
using AnyFS.Sdk;
using AnyFS.Sources.Common;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.Artifactory;

/// <summary>
/// The Artifactory source's walks (spec 7.13): the count, the stamps that let a walk go only where something changed, and
/// the reading of a folder with everything below it in one request.
/// </summary>
public sealed partial class ArtifactorySource
{
    // How far behind the server's clock a listing is taken to be as of. A file put while a folder is listed shows in the
    // answer or not, as the server has it; one that shows a moment after its own time (written by another of the server's
    // nodes, whose clock is a little off; saved as the listing was read) is then still newer than the listing's time, and
    // the next walk finds it. Folders changed within it are listed by two walks running: a few requests.
    private static readonly TimeSpan AsOfMargin = TimeSpan.FromMinutes(5);

    // how many folders are remembered as holding too much to read at once; past that the memory starts again
    private const int MostTooBig = 100_000;

    // a repository's check in the walk going on, or the last one: by the path of the repository's root in the source
    private readonly ConcurrentDictionary<string, Check> _checks = new(StringComparer.Ordinal);

    // the folders found to hold more than a read at once takes, by repository and path: not tried again in this run
    private readonly ConcurrentDictionary<string, byte> _tooBig = new(StringComparer.Ordinal);

    private PartsCounted? _counted;

    /// <summary>
    /// What a walk learnt of a repository as it came to it: the time its kept listings were as of (<see cref="Floor"/>),
    /// the time this walk's are (<see cref="AsOf"/>), and the folders with something added or changed in or below them
    /// between the two (null: too much to say, the repository is read again). Once the walk has been through the repository
    /// with nothing stopping it or failing, the next takes <see cref="AsOf"/> for the floor.
    /// </summary>
    private sealed class Check(string floor, string asOf, HashSet<string>? changed) : IDisposable
    {
        public string Floor { get; } = floor;

        public string AsOf { get; } = asOf;

        public HashSet<string>? Changed { get; } = changed;

        public volatile bool Failed;

        public volatile bool Stopped;

        public CancellationTokenRegistration Stopping { get; set; }

        public bool WentEverywhere => !Failed && !Stopped;

        /// <summary>Whether something was added or changed in the folder or below it (its path in the repository).</summary>
        public bool Holds(string path) => Changed is null || Changed.Contains(path);

        public void Dispose() => Stopping.Dispose();
    }

    // --- counting ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// "Count files on start": the first walk of the tree (<see cref="KeptListings"/>), which lists every folder and keeps
    /// the listings, so they open at once afterwards; with listings kept, a walk again every "Walk every folder every"
    /// minutes. A server's count says how far it has got through its repositories.
    /// </summary>
    public async Task CountFilesAsync(CancellationToken ct)
    {
        if (Scope == ArtifactoryScope.Server)
        {
            Context.ReportProgress("Counting files: listing the repositories");
            long? total = null;
            try
            {
                total = (await ShownAsync(ct).ConfigureAwait(false)).Count;
            }
            catch (VfsException ex)
            {
                Logger.LogDebug("artifactory: how many repositories there are to count is not known: {Error}", ex.Message);
            }

            Volatile.Write(ref _counted, new PartsCounted(Context, total, "repositories"));
        }

        try
        {
            await Kept.CountAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _counted, null)?.Over();
        }
    }

    /// <summary>The listings as kept, for tests.</summary>
    internal ListingCache Listings => Kept.Cache;

    /// <summary>One walk now (a test's: the walks after the count come by the clock), stamps kept unless <paramref name="full"/>.</summary>
    internal Task<bool> WalkAsync(SourceCounters counters, bool full, CancellationToken ct) => Kept.WalkAsync(counters, full, ct);

    // --- stamps: what changed since the last walk --------------------------------------------------------------------------

    /// <summary>The server's time now, less the margin, as the query language takes a time: what a listing made now is as of.</summary>
    private string Now() => (Session.ServerNow - AsOfMargin).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>
    /// A folder's stamp for the walks (<see cref="KeptListingsOptions.StampOf"/>): the time its listing is as of. A
    /// repository's root is where a walk asks Artifactory, in one query, what was added or changed in the repository since
    /// its listings were as of (<see cref="CheckAsync"/>); a folder with nothing of that in or below it gets the stamp it
    /// has, and is kept with every folder below it; one with something gets the walk's time, and is listed again. A
    /// repository nothing happened in is one request a walk. The server's root has none: its listing is the repositories.
    /// What a query does not tell is a file deleted: that shows when a walk lists every folder whatever its stamp, once a day.
    /// </summary>
    internal async ValueTask<string?> StampAsync(string path, string? held, CancellationToken ct)
    {
        var found = await FindAsync(path, ct).ConfigureAwait(false);
        var root = found.Path == found.Root;
        if (root && found.Repository is not null)
        {
            Volatile.Read(ref _counted)?.Reached(found.Repository.Key);
        }

        if (found.Repository is not { } repository || !Asked(repository))
        {
            // the server's root, whose listing is the repositories; a virtual repository, which no query tells the changes of
            return null;
        }

        if (held is null)
        {
            // nothing held of it: listed as it stands now
            if (root && _checks.TryRemove(found.Root, out var gone))
            {
                gone.Dispose();
            }

            return _checks.TryGetValue(found.Root, out var going) ? going.AsOf : Now();
        }

        if (!root)
        {
            return _checks.TryGetValue(found.Root, out var check) && check.Holds(found.Own) ? check.AsOf : held;
        }

        return await CheckAsync(found, repository, held, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// A walk at a repository's root: asks what was added or changed since the listings kept were as of. That time is the
    /// first part of the root's stamp, and moves on only once a walk has been everywhere the answer pointed with nothing
    /// stopping it or failing; a walk cut short, or one a folder could not be listed in, leaves it, and the next asks from
    /// the same time again. Nothing changed: the stamp held, and the repository is kept whole.
    /// </summary>
    private async ValueTask<string?> CheckAsync(Found found, ArtRepository repository, string held, CancellationToken ct)
    {
        var bar = held.IndexOf('|');
        var floor = bar < 0 ? held : held[..bar];
        var moved = false;
        if (_checks.TryGetValue(found.Root, out var last) && last.WentEverywhere && last.Floor == floor && last.Changed is not { Count: 0 })
        {
            // the walk before went everywhere something had changed: the listings are as of then
            floor = last.AsOf;
            moved = true;
        }

        var asOf = Now();
        HashSet<string>? changed;
        var failed = false;
        try
        {
            changed = await Guard(() => ChangedSinceAsync(repository, found.Own, floor, ct)).ConfigureAwait(false);
        }
        catch (VfsException ex)
        {
            Logger.LogInformation("artifactory: what changed in {Repository} since {Since} could not be asked ({Error}); its folders are kept as they are until the next walk", repository.Key, floor, ex.Message);
            changed = [];
            failed = true;
            moved = false;
            floor = bar < 0 ? held : held[..bar];
        }

        var check = new Check(floor, asOf, changed) { Failed = failed };
        if (ct.CanBeCanceled)
        {
            check.Stopping = ct.Register(static state => ((Check)state!).Stopped = true, check);
        }

        _checks[found.Root] = check;
        last?.Dispose();
        if (changed is { Count: 0 })
        {
            if (moved && Kept.Cache.Held(found.Path) is { } listing)
            {
                // the time moved on, and nothing to list for it: written with the listing as it is
                Kept.Cache.Keep(found.Path, listing, floor);
            }

            return held;
        }

        Logger.LogDebug("artifactory: {Repository} since {Since}: {Changed}", repository.Key, floor, changed is null ? "too much changed to go folder by folder; read again" : $"{changed.Count:N0} folders hold something added or changed");
        return $"{floor}|{asOf}";
    }

    /// <summary>
    /// The folders of a repository (below <paramref name="within"/>, where the source is a folder of it) with something
    /// created, modified or updated in or below them since a time, each with every folder above it: one query. Null where
    /// that is more items than <see cref="ChangesMost"/>, or than the server gives in one answer.
    /// </summary>
    private async Task<HashSet<string>?> ChangedSinceAsync(ArtRepository repository, string within, string since, CancellationToken ct)
    {
        var time = Quoted(since);
        var times = $"\"$or\":[{{\"created\":{{\"$gt\":{time}}}}},{{\"modified\":{{\"$gt\":{time}}}}},{{\"updated\":{{\"$gt\":{time}}}}}]";
        var criteria = within.Length == 0
            ? times
            : $"\"$and\":[{{{times}}},{{{Below(within)}}}]";
        var query = $"items.find({{\"repo\":{Quoted(repository.StorageKey)},\"type\":\"any\",{criteria}}}).include(\"repo\",\"path\",\"name\",\"type\")";
        var answer = await Api.AqlAsync(query, ChangesMost, $"what changed in {repository.Key}", ct, streamed: true).ConfigureAwait(false);
        if (answer.More)
        {
            return null;
        }

        var changed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in answer.Items)
        {
            if (!Under(item.Folder, within))
            {
                continue;
            }

            if (item.IsFolder)
            {
                changed.Add(item.Path);
            }

            // its folder and every folder above that, up to the root: one already there has those above it there too
            for (var folder = item.Folder; changed.Add(folder) && folder.Length > within.Length; folder = folder[..Math.Max(0, folder.LastIndexOf('/'))])
            {
            }
        }

        return changed;
    }

    /// <summary>Whether a path in a repository is a folder or below it (the root holds every path).</summary>
    private static bool Under(string path, string folder)
        => folder.Length == 0 || path.Equals(folder, StringComparison.Ordinal) || path.StartsWith(folder, StringComparison.Ordinal) && path.Length > folder.Length && path[folder.Length] == '/';

    /// <summary>The criteria for the items in a folder and in every folder below it.</summary>
    private static string Below(string folder) => $"\"$or\":[{{\"path\":{Quoted(folder)}}},{{\"path\":{{\"$match\":{Quoted(folder + "/*")}}}}}]";

    /// <summary>A folder's listing failed: the walk has not been everywhere in its repository, unless the folder is gone or kept from the account.</summary>
    private void NoteFailure(string root, Exception ex)
    {
        if (ex is VfsException { Error: VfsError.NotFound or VfsError.NotADirectory or VfsError.AccessDenied })
        {
            return;
        }

        if (_checks.TryGetValue(root, out var check))
        {
            check.Failed = true;
        }
    }

    // --- a folder with all below it, at once ------------------------------------------------------------------------------

    /// <summary>
    /// For a walk (<see cref="KeptListingsOptions.ListTree"/>): a folder's listing and that of every folder below it from one
    /// query, where they hold no more than <see cref="TreeMost"/> items together; null for a folder that holds more (it is
    /// listed alone, and each folder in it tried the same way, so a repository of any size is read in pieces of that size),
    /// for the server's root, and for a folder kept that has something added or changed below it: only the folders that
    /// hold the change are listed again, each alone.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<VfsEntry>>?> ListTreeAsync(string path, CancellationToken ct)
    {
        var found = await FindAsync(path, ct).ConfigureAwait(false);
        if (found.Repository is not { } repository || !Asked(repository) || found.Entry is { IsDirectory: false })
        {
            return null;
        }

        // a * or a ? in the folder's own path would be taken for any characters
        var key = $"{repository.StorageKey}/{found.Own}";
        if (_tooBig.ContainsKey(key) || found.Own.AsSpan().IndexOfAny('*', '?') >= 0)
        {
            return null;
        }

        if (_checks.TryGetValue(found.Root, out var check) && check.Changed is { } changed && changed.Contains(found.Own) && Kept.Cache.Held(found.Path) is not null)
        {
            return null;
        }

        var query = $"items.find({{\"repo\":{Quoted(repository.StorageKey)},\"type\":\"any\"{(found.Own.Length == 0 ? "" : "," + Below(found.Own))}}}).include({Fields})";
        AqlAnswer answer;
        try
        {
            answer = await Guard(() => Api.AqlAsync(query, TreeMost, $"everything in {repository.Key}/{found.Own}", ct, streamed: true)).ConfigureAwait(false);
        }
        catch (VfsException ex)
        {
            // the folder is listed alone, and says for itself what is wrong with it
            Logger.LogDebug("artifactory: {Repository}/{Path} could not be read at once: {Error}", repository.Key, found.Own, ex.Message);
            return null;
        }

        if (answer.More)
        {
            if (_tooBig.Count >= MostTooBig)
            {
                _tooBig.Clear();
            }

            _tooBig[key] = 0;
            return null;
        }

        return ListingsOf(found.Path, found.Own, answer.Items);
    }

    /// <summary>The listings of a folder and of every folder below it, by their paths in the source, from the items a query gave.</summary>
    private static Dictionary<string, IReadOnlyList<VfsEntry>> ListingsOf(string path, string own, List<ArtItem> items)
    {
        var byFolder = new Dictionary<string, List<ArtItem>>(StringComparer.Ordinal) { [own] = [] };
        foreach (var item in items)
        {
            if (!Under(item.Folder, own))
            {
                continue;
            }

            if (!byFolder.TryGetValue(item.Folder, out var siblings))
            {
                byFolder[item.Folder] = siblings = [];
            }

            siblings.Add(item);
            if (item.IsFolder)
            {
                byFolder.TryAdd(item.Path, []);
            }
        }

        var listings = new Dictionary<string, IReadOnlyList<VfsEntry>>(byFolder.Count, StringComparer.Ordinal);
        var pending = new Stack<(string Path, string Own)>();
        pending.Push((path, own));
        while (pending.TryPop(out var next))
        {
            var entries = ArtifactoryNames.Entries(byFolder.GetValueOrDefault(next.Own) ?? []);
            listings[next.Path] = entries;
            foreach (var entry in entries)
            {
                if (entry.IsDirectory)
                {
                    pending.Push((VfsPath.Join(next.Path, entry.Name), next.Own.Length == 0 ? ArtifactoryNames.Own(entry) : next.Own + "/" + ArtifactoryNames.Own(entry)));
                }
            }
        }

        return listings;
    }
}
