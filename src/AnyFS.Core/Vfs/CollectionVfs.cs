using System.Runtime.CompilerServices;
using AnyFS.Core.Runtime;
using AnyFS.Core.Stats;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging;

namespace AnyFS.Core.Vfs;

/// <summary>
/// The router (spec 4, 4.1, 4.2): synthesises one tree per collection from its members' source paths,
/// routes every call to the right source, enforces the read-only layers, falls back to a unique
/// case-insensitive match inside case-sensitive sources, and counts stats on the way through.
/// </summary>
public sealed class CollectionVfs : IVirtualFileSystem
{
    private readonly ILogger _logger;
    private readonly StatsCollector? _stats;
    private readonly ListingCache _fallbackListings;
    private volatile SyntheticTree _tree = SyntheticTree.Empty;

    public CollectionVfs(Guid id, string name, StatsCollector? stats, ILogger logger)
    {
        Id = id;
        CollectionName = name;
        _stats = stats;
        _logger = logger;
        _fallbackListings = new ListingCache(TimeSpan.FromSeconds(2), ListForFallbackAsync);
    }

    public Guid Id { get; }

    public string CollectionName { get; private set; }

    public bool ReadOnly => false;

    public SyntheticTree Tree => _tree;

    public void Rename(string name) => CollectionName = name;

    /// <summary>Rebuilds the synthetic tree; enabling or disabling a source never restarts a mount.</summary>
    public void Update(IEnumerable<CollectionMember> members)
    {
        _tree = SyntheticTree.Build(members);
        _fallbackListings.InvalidateAll();
    }

    /// <summary>The view a mount gets: this, or a wrapper that refuses every write when the mount is read-only.</summary>
    public IVirtualFileSystem ForMount(bool readOnly) => readOnly ? new MountVfs(this) : this;

    public bool CanWrite(string path)
    {
        if (!VfsPath.TryNormalize(path, out var normalized, out _))
        {
            return false;
        }

        var segments = VfsPath.Split(normalized);
        var (node, depth) = _tree.Resolve(segments);
        if (depth == segments.Length)
        {
            return false;
        }

        return node.Attachments.Any(a => a.Source.IsUsable && WriteRefusal(a) is null);
    }

    public async Task<VfsEntry?> StatAsync(string path, CancellationToken ct)
    {
        path = VfsPath.Normalize(path);
        var target = await ResolveAsync(path, ct).ConfigureAwait(false);
        if (target is null)
        {
            return null;
        }

        return target.Attachment is null ? await SyntheticEntryAsync(target.Node, ct).ConfigureAwait(false) : target.Entry;
    }

    public async IAsyncEnumerable<VfsEntry> ListAsync(string path, [EnumeratorCancellation] CancellationToken ct)
    {
        path = VfsPath.Normalize(path);
        var target = await ResolveAsync(path, ct).ConfigureAwait(false) ?? throw VfsException.NotFound(path);
        if (target.Attachment is not null)
        {
            if (target.Entry is { IsDirectory: false })
            {
                throw VfsException.NotADirectory(path);
            }

            var files = 0;
            await foreach (var entry in target.Attachment.Source.Current!.ListAsync(target.Remaining, ct).ConfigureAwait(false))
            {
                if (!entry.IsDirectory)
                {
                    files++;
                }

                yield return entry;
            }

            target.Attachment.Source.RecordListing(target.Remaining, files);
            yield break;
        }

        var node = target.Node;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var yielded = 0;
        foreach (var child in node.Children)
        {
            seen.Add(child.Name);
            yielded++;
            yield return await SyntheticEntryAsync(child, ct).ConfigureAwait(false);
        }

        var usable = 0;
        foreach (var attachment in node.Attachments)
        {
            var source = attachment.Source.Current;
            if (source is null || !attachment.Source.IsUsable)
            {
                continue;
            }

            usable++;
            List<VfsEntry> entries = [];
            try
            {
                await foreach (var entry in source.ListAsync("", ct).ConfigureAwait(false))
                {
                    entries.Add(entry);
                }

                attachment.Source.ClearNotes($"list:{Id:N}:");
            }
            catch (VfsException ex)
            {
                if (attachment.Source.AddNote($"list:{Id:N}:{node.Path}", $"root listing failed in collection '{CollectionName}': {ex.Message}"))
                {
                    _logger.LogWarning("collection {Collection}: listing the root of source {Source} at '{Path}' failed: {Error}", CollectionName, attachment.Source.Name, node.Path, ex.Message);
                }

                continue;
            }

            var files = 0;
            foreach (var entry in entries)
            {
                if (!seen.Add(entry.Name))
                {
                    var by = node.HasChild(entry.Name) ? "a folder of the collection layout" : "another source at the same path";
                    if (attachment.Source.AddNote($"shadow:{Id:N}:{node.Path}:{entry.Name}", $"'{VfsPath.Join(node.Path, entry.Name)}' is hidden in collection '{CollectionName}' by {by}"))
                    {
                        _logger.LogWarning("collection {Collection}: '{Entry}' from source {Source} is hidden by {By}", CollectionName, VfsPath.Join(node.Path, entry.Name), attachment.Source.Name, by);
                    }

                    continue;
                }

                if (!entry.IsDirectory)
                {
                    files++;
                }

                yielded++;
                yield return entry;
            }

            attachment.Source.RecordListing("", files);
        }

        if (yielded == 0 && usable == 0 && node.Attachments.Count > 0)
        {
            throw VfsException.Unavailable(Describe(node.Attachments));
        }
    }

    public async Task<IVfsFile> OpenAsync(string path, VfsOpenMode mode, CancellationToken ct)
    {
        path = VfsPath.Normalize(path);
        var target = await ResolveAsync(path, ct).ConfigureAwait(false);
        if (target is { Attachment: null })
        {
            throw VfsException.IsADirectory(path);
        }

        if (target is null)
        {
            if (!mode.MayCreate())
            {
                throw VfsException.NotFound(path);
            }

            target = await ResolveForCreateAsync(path, ct).ConfigureAwait(false);
        }
        else if (mode == VfsOpenMode.CreateNew)
        {
            throw VfsException.AlreadyExists(path);
        }
        else if (target.Entry is { IsDirectory: true })
        {
            throw VfsException.IsADirectory(path);
        }

        if (mode.IsWrite())
        {
            EnsureWritable(target.Attachment!, path);
        }

        var file = await target.Attachment!.Source.Current!.OpenAsync(target.Remaining, mode, ct).ConfigureAwait(false);
        if (mode.MayCreate())
        {
            InvalidateFallback(target.Attachment, VfsPath.Parent(target.Remaining));
        }

        return _stats is null ? file : new CountingVfsFile(file, _stats, CollectionName);
    }

    public async Task CreateDirectoryAsync(string path, CancellationToken ct)
    {
        path = VfsPath.Normalize(path);
        if (await ResolveAsync(path, ct).ConfigureAwait(false) is not null)
        {
            throw VfsException.AlreadyExists(path);
        }

        var target = await ResolveForCreateAsync(path, ct).ConfigureAwait(false);
        EnsureWritable(target.Attachment!, path);
        await target.Attachment!.Source.Current!.CreateDirectoryAsync(target.Remaining, ct).ConfigureAwait(false);
        InvalidateFallback(target.Attachment, VfsPath.Parent(target.Remaining));
    }

    public async Task DeleteAsync(string path, CancellationToken ct)
    {
        path = VfsPath.Normalize(path);
        var target = await ResolveAsync(path, ct).ConfigureAwait(false) ?? throw VfsException.NotFound(path);
        if (target.Attachment is null)
        {
            throw VfsException.ReadOnly(path, "this folder is part of the collection layout");
        }

        EnsureWritable(target.Attachment, path);
        await target.Attachment.Source.Current!.DeleteAsync(target.Remaining, ct).ConfigureAwait(false);
        InvalidateFallback(target.Attachment, VfsPath.Parent(target.Remaining));
    }

    public async Task RenameAsync(string fromPath, string toPath, bool replace, CancellationToken ct)
    {
        fromPath = VfsPath.Normalize(fromPath);
        toPath = VfsPath.Normalize(toPath);
        var from = await ResolveAsync(fromPath, ct).ConfigureAwait(false) ?? throw VfsException.NotFound(fromPath);
        if (from.Attachment is null)
        {
            throw VfsException.ReadOnly(fromPath, "this folder is part of the collection layout");
        }

        var to = await ResolveAsync(toPath, ct).ConfigureAwait(false);
        if (to is not null)
        {
            if (to.Attachment is null || !replace || to.Entry is { IsDirectory: true })
            {
                throw VfsException.AlreadyExists(toPath);
            }
        }
        else
        {
            to = await ResolveForCreateAsync(toPath, ct).ConfigureAwait(false);
        }

        if (!SameAttachment(from.Attachment, to.Attachment!))
        {
            throw new VfsException(VfsError.NotSupported, $"'{fromPath}' and '{toPath}' are in different sources; copy then delete instead");
        }

        EnsureWritable(from.Attachment, fromPath);
        EnsureWritable(to.Attachment!, toPath);
        await from.Attachment.Source.Current!.RenameAsync(from.Remaining, to.Remaining, replace, ct).ConfigureAwait(false);
        InvalidateFallback(from.Attachment, VfsPath.Parent(from.Remaining));
        InvalidateFallback(to.Attachment!, VfsPath.Parent(to.Remaining));
    }

    public async Task SetTimesAsync(string path, DateTimeOffset? created, DateTimeOffset? modified, CancellationToken ct)
    {
        path = VfsPath.Normalize(path);
        var target = await ResolveAsync(path, ct).ConfigureAwait(false) ?? throw VfsException.NotFound(path);
        if (target.Attachment is null)
        {
            throw VfsException.ReadOnly(path, "this folder is part of the collection layout");
        }

        EnsureWritable(target.Attachment, path);
        await target.Attachment.Source.Current!.SetTimesAsync(target.Remaining, created, modified, ct).ConfigureAwait(false);
    }

    public async Task<VfsSpace?> GetSpaceAsync(string path, CancellationToken ct)
    {
        path = VfsPath.Normalize(path);
        var target = await ResolveAsync(path, ct).ConfigureAwait(false);
        if (target is null)
        {
            return null;
        }

        if (target.Attachment is not null)
        {
            return await target.Attachment.Source.Current!.GetSpaceAsync(target.Remaining, ct).ConfigureAwait(false);
        }

        foreach (var attachment in target.Node.Attachments.Where(a => a.Source.IsUsable))
        {
            var space = await attachment.Source.Current!.GetSpaceAsync("", ct).ConfigureAwait(false);
            if (space is not null)
            {
                return space;
            }
        }

        return null;
    }

    /// <summary>Why a write to this attachment would be refused (membership, then capability), or null when allowed.</summary>
    internal string? WriteRefusal(SyntheticTree.Attachment attachment)
    {
        if (attachment.ReadOnly)
        {
            return $"'{attachment.Source.Name}' is read-only in collection '{CollectionName}'";
        }

        if (!attachment.Source.Capabilities.CanWrite)
        {
            return $"'{attachment.Source.Name}' can't write";
        }

        return null;
    }

    private void EnsureWritable(SyntheticTree.Attachment attachment, string path)
    {
        var refusal = WriteRefusal(attachment);
        if (refusal is not null)
        {
            throw VfsException.ReadOnly(path, refusal);
        }
    }

    private static bool SameAttachment(SyntheticTree.Attachment a, SyntheticTree.Attachment b)
        => a.Source.Id == b.Source.Id && a.MountPath.Equals(b.MountPath, StringComparison.OrdinalIgnoreCase);

    private async Task<Target?> ResolveAsync(string path, CancellationToken ct)
    {
        var tree = _tree;
        var segments = VfsPath.Split(path);
        var (node, depth) = tree.Resolve(segments);
        if (depth == segments.Length)
        {
            return new Target(node, null, "", null);
        }

        var remaining = string.Join('/', segments, depth, segments.Length - depth);
        var head = segments[depth];
        var usable = 0;
        foreach (var attachment in node.Attachments)
        {
            var source = attachment.Source.Current;
            if (source is null || !attachment.Source.IsUsable)
            {
                continue;
            }

            usable++;
            // the first source (by name) that has the first segment owns that whole subtree, exactly as the
            // listing shows it; a later source's same-named entry is shadowed, so it is never reachable by path
            var owner = await StatInSourceAsync(attachment, source, head, ct).ConfigureAwait(false);
            if (owner is null)
            {
                continue;
            }

            if (depth == segments.Length - 1)
            {
                return new Target(node, attachment, owner.Value.Path, owner.Value.Entry);
            }

            var hit = await StatInSourceAsync(attachment, source, VfsPath.Join(owner.Value.Path, string.Join('/', segments, depth + 1, segments.Length - depth - 1)), ct).ConfigureAwait(false);
            return hit is null ? null : new Target(node, attachment, hit.Value.Path, hit.Value.Entry);
        }

        if (usable == 0 && node.Attachments.Count > 0)
        {
            throw VfsException.Unavailable(Describe(node.Attachments));
        }

        return null;
    }

    /// <summary>Where a new entry would go: inside the attachment that holds its parent, or the first writable attachment at a synthetic node.</summary>
    private async Task<Target> ResolveForCreateAsync(string path, CancellationToken ct)
    {
        var (parentPath, name) = VfsPath.SplitLast(path);
        var parent = await ResolveAsync(parentPath, ct).ConfigureAwait(false) ?? throw VfsException.NotFound(parentPath);
        if (parent.Attachment is not null)
        {
            if (parent.Entry is { IsDirectory: false })
            {
                throw VfsException.NotADirectory(parentPath);
            }

            return new Target(parent.Node, parent.Attachment, VfsPath.Join(parent.Remaining, name), null);
        }

        var node = parent.Node;
        if (node.HasChild(name))
        {
            throw VfsException.AlreadyExists(path);
        }

        if (node.Attachments.Count == 0)
        {
            throw VfsException.ReadOnly(path, "this folder is part of the collection layout");
        }

        string? refusal = null;
        var usable = 0;
        foreach (var attachment in node.Attachments)
        {
            if (!attachment.Source.IsUsable)
            {
                continue;
            }

            usable++;
            var reason = WriteRefusal(attachment);
            if (reason is null)
            {
                return new Target(node, attachment, name, null);
            }

            refusal ??= reason;
        }

        throw usable == 0 ? VfsException.Unavailable(Describe(node.Attachments)) : VfsException.ReadOnly(path, refusal!);
    }

    /// <summary>Exact stat first; then, for a case-sensitive source, a walk accepting a unique case-insensitive match per segment.</summary>
    private async Task<(string Path, VfsEntry Entry)?> StatInSourceAsync(SyntheticTree.Attachment attachment, ISource source, string remaining, CancellationToken ct)
    {
        var entry = await source.StatAsync(remaining, ct).ConfigureAwait(false);
        if (entry is not null)
        {
            return (remaining, entry);
        }

        if (source.Capabilities.CaseInsensitive)
        {
            return null;
        }

        var current = "";
        VfsEntry? last = null;
        foreach (var segment in VfsPath.Split(remaining))
        {
            IReadOnlyList<VfsEntry> listing;
            try
            {
                listing = await _fallbackListings.GetAsync(FallbackKey(attachment.Source.Id, current), ct).ConfigureAwait(false);
            }
            catch (VfsException ex) when (ex.Error is VfsError.NotFound or VfsError.NotADirectory)
            {
                return null;
            }

            var match = listing.FirstOrDefault(e => e.Name.Equals(segment, StringComparison.Ordinal));
            if (match is null)
            {
                var candidates = listing.Where(e => e.Name.Equals(segment, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
                if (candidates.Count != 1)
                {
                    return null;
                }

                match = candidates[0];
            }

            current = VfsPath.Join(current, match.Name);
            last = match;
        }

        return last is null ? null : (current, last);
    }

    private async Task<VfsEntry> SyntheticEntryAsync(SyntheticTree.Node node, CancellationToken ct)
    {
        DateTimeOffset? newest = null;
        foreach (var attachment in node.Attachments)
        {
            var source = attachment.Source.Current;
            if (source is null || !attachment.Source.IsUsable)
            {
                continue;
            }

            try
            {
                var root = await source.StatAsync("", ct).ConfigureAwait(false);
                if (root?.Modified is { } modified && (newest is null || modified > newest))
                {
                    newest = modified;
                }
            }
            catch (VfsException)
            {
            }
        }

        return new VfsEntry(node.Name, true, 0, null, newest, VfsAttributes.ReadOnly);
    }

    private static string Describe(IReadOnlyList<SyntheticTree.Attachment> attachments)
    {
        var first = attachments[0].Source;
        var status = first.Status;
        return attachments.Count == 1
            ? $"source '{first.Name}' is {status.State.ToString().ToLowerInvariant()}{(status.Text.Length > 0 ? ": " + status.Text : "")}"
            : $"none of the {attachments.Count} sources here is available yet";
    }

    private static string FallbackKey(Guid sourceId, string path) => sourceId.ToString("N") + "|" + path;

    private void InvalidateFallback(SyntheticTree.Attachment attachment, string directory)
        => _fallbackListings.Invalidate(FallbackKey(attachment.Source.Id, directory));

    private IAsyncEnumerable<VfsEntry> ListForFallbackAsync(string key, CancellationToken ct)
    {
        var separator = key.IndexOf('|');
        var id = Guid.ParseExact(key[..separator], "N");
        var path = key[(separator + 1)..];
        var member = _tree.Members.FirstOrDefault(m => m.Source.Id == id);
        var source = member?.Source.Current ?? throw VfsException.Unavailable("the source is no longer part of this collection");
        return source.ListAsync(path, ct);
    }

    /// <summary>What a path resolved to: a synthetic node (Attachment null) or a path inside one source.</summary>
    private sealed record Target(SyntheticTree.Node Node, SyntheticTree.Attachment? Attachment, string Remaining, VfsEntry? Entry);
}

/// <summary>The read-only mount's view: every write is refused before it reaches the collection (the third layer).</summary>
public sealed class MountVfs(CollectionVfs inner) : IVirtualFileSystem
{
    public string CollectionName => inner.CollectionName;

    public bool ReadOnly => true;

    public bool CanWrite(string path) => false;

    public Task<VfsEntry?> StatAsync(string path, CancellationToken ct) => inner.StatAsync(path, ct);

    public IAsyncEnumerable<VfsEntry> ListAsync(string path, CancellationToken ct) => inner.ListAsync(path, ct);

    public Task<IVfsFile> OpenAsync(string path, VfsOpenMode mode, CancellationToken ct)
        => mode.IsWrite() ? throw Refuse(path) : inner.OpenAsync(path, mode, ct);

    public Task CreateDirectoryAsync(string path, CancellationToken ct) => throw Refuse(path);

    public Task DeleteAsync(string path, CancellationToken ct) => throw Refuse(path);

    public Task RenameAsync(string fromPath, string toPath, bool replace, CancellationToken ct) => throw Refuse(fromPath);

    public Task SetTimesAsync(string path, DateTimeOffset? created, DateTimeOffset? modified, CancellationToken ct) => throw Refuse(path);

    public Task<VfsSpace?> GetSpaceAsync(string path, CancellationToken ct) => inner.GetSpaceAsync(path, ct);

    private static VfsException Refuse(string path) => VfsException.ReadOnly(path, "the mount is read-only");
}

/// <summary>Counts "files served" (first byte of a read handle, once per open) and bytes read/written (spec 11.3).</summary>
public sealed class CountingVfsFile(IVfsFile inner, StatsCollector stats, string collection) : IVfsFile
{
    private int _served;

    public long Length => inner.Length;

    public async ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct)
    {
        var n = await inner.ReadAsync(offset, buffer, ct).ConfigureAwait(false);
        if (n > 0)
        {
            if (Interlocked.Exchange(ref _served, 1) == 0)
            {
                stats.FileServed(collection);
            }

            stats.BytesRead(collection, n);
        }

        return n;
    }

    public async ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        await inner.WriteAsync(offset, data, ct).ConfigureAwait(false);
        stats.BytesWritten(collection, data.Length);
    }

    public ValueTask SetLengthAsync(long length, CancellationToken ct) => inner.SetLengthAsync(length, ct);

    public ValueTask FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
