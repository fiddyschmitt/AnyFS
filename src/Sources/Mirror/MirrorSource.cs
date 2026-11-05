using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace AnyFS.Sources.Mirror;

/// <summary>
/// System.IO throughout: FileStream-free ranged reads through <see cref="RandomAccess"/> on handles opened
/// with FileShare.ReadWrite | Delete. Status: Ready when every root is reachable, Degraded naming the ones
/// that aren't, Failed only when none is. Writes go through when the collection and mount allow them. Each
/// question put to the file system is told to <see cref="ISourceContext.UpstreamWatcher"/>, by the real path: a
/// listing, a path's metadata, an open file from its open to its close, the check that a mirrored path is there.
/// </summary>
public sealed class MirrorSource : SourceBase, ICountable
{
    private static readonly EnumerationOptions Enumeration = new() { AttributesToSkip = 0, IgnoreInaccessible = true, ReturnSpecialDirectories = false };

    /// <summary>The most threads per path, and paths at a time, a count takes (<see cref="MirrorSettings.CountThreadsPerPath"/>, <see cref="MirrorSettings.CountPathsInParallel"/>).</summary>
    internal const int MaxCountParallelism = 64;

    /// <summary>How many entries a counting thread sees between updates of <see cref="SourceBase.Counters"/>.</summary>
    private const int CountBatch = 1024;

    // what the file system is asked, in the words the Operations tab uses for a source's calls
    private const string ListFolder = "List folder";
    private const string GetMetadata = "Get metadata";

    private readonly MirrorSettings _settings;
    private readonly Node _root = new("");
    private readonly List<MirrorRoot> _roots = [];
    private readonly HashSet<MirrorRoot> _unreachable = [];
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _checker;
    private IUpstreamWatcher? _watcher;

    public MirrorSource(MirrorSettings settings)
    {
        _settings = settings;
    }

    public override SourceCapabilities Capabilities { get; } = new(CanWrite: true, CaseInsensitive: OperatingSystem.IsWindows() || OperatingSystem.IsMacOS());

    public IReadOnlyList<MirrorRoot> Roots => _roots;

    public IReadOnlyCollection<MirrorRoot> Unreachable
    {
        get
        {
            lock (_gate)
            {
                return _unreachable.ToList();
            }
        }
    }

    /// <summary>For tests: re-check reachability now.</summary>
    public void CheckRoots() => CheckRootsCore(report: true);

    /// <summary>For tests: called on a counting thread as it starts listing a folder of a path.</summary>
    internal Action<MirrorRoot, string>? CountListing { get; set; }

    /// <summary>
    /// "Count files on start": <see cref="MirrorSettings.CountPathsInParallel"/> of the configured paths are walked at a time
    /// (8 by default), each by <see cref="MirrorSettings.CountThreadsPerPath"/> threads of its own (8) that list its folders
    /// in parallel, with <see cref="SourceBase.Counters"/> updated as the walks go. The threads are the walk's, not the
    /// thread pool's: a listing blocks on the disk or the share, and 64 of them would starve the pool the rest of AnyFS
    /// runs on. A path configured inside another (C:\ and C:\Photos) is counted once, by its own walk; junctions and
    /// symlinks are counted but not followed; an unreachable path contributes nothing until the next count.
    /// </summary>
    public async Task CountFilesAsync(CancellationToken ct)
    {
        // every level of the folder chains is a folder of the source
        Counters.Set(0, CountNodes(_root, underRoot: _root.Root is not null), EnumerationState.InProgress);
        var chains = new HashSet<string>(_roots.Select(r => r.ChainPath), StringComparer.OrdinalIgnoreCase);
        var threads = Math.Clamp(_settings.CountThreadsPerPath, 1, MaxCountParallelism);
        using var turns = new SemaphoreSlim(Math.Clamp(_settings.CountPathsInParallel, 1, MaxCountParallelism));
        var walks = _roots.Select(async root =>
        {
            await turns.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await WalkForCountAsync(root, chains, threads, ct).ConfigureAwait(false);
            }
            finally
            {
                turns.Release();
            }
        }).ToList();
        await Task.WhenAll(walks).ConfigureAwait(false);
    }

    /// <summary>Chain levels the walks won't see: those above any configured path, and configured paths inside another (which the outer walk skips).</summary>
    private static long CountNodes(Node node, bool underRoot)
        => node.Children.Values.Sum(child => (child.Root is not null || !underRoot ? 1 : 0) + CountNodes(child, underRoot || child.Root is not null));

    /// <summary>
    /// One path's count: <paramref name="threads"/> threads take its folders from one stack (deepest first, so the stack
    /// stays short), list them and put the subfolders back, until none is left or being listed.
    /// </summary>
    private async Task WalkForCountAsync(MirrorRoot root, HashSet<string> chains, int threads, CancellationToken ct)
    {
        if (!IsThere(root))
        {
            Logger.LogDebug("mirror: count skipped {Path}: not reachable", root.Original);
            return;
        }

        using var folders = new BlockingCollection<(string Real, string Chain)>(new ConcurrentStack<(string Real, string Chain)>());
        var outstanding = 1;
        folders.Add((root.FullPath, root.ChainPath), ct);

        void Count()
        {
            long files = 0, directories = 0;
            var pending = 0;
            try
            {
                foreach (var (real, chain) in folders.GetConsumingEnumerable(ct))
                {
                    using var at = Counters.EnterFolder(chain);
                    var asked = _watcher?.Asked(ListFolder, real);
                    long entries = 0;
                    try
                    {
                        CountListing?.Invoke(root, real);
                        foreach (var entry in new DirectoryInfo(real).EnumerateFileSystemInfos("*", Enumeration))
                        {
                            entries++;
                            if (entry.Attributes.HasFlag(FileAttributes.Directory))
                            {
                                var childChain = chain.Length == 0 ? entry.Name : chain + "/" + entry.Name;
                                if (chains.Count > 1 && chains.Contains(childChain))
                                {
                                    // another configured path appears here and walks it (and it's a chain node, counted already)
                                    continue;
                                }

                                directories++;
                                if (!entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                                {
                                    // counted as outstanding before it is put back, so the walk cannot end in between
                                    Interlocked.Increment(ref outstanding);
                                    folders.Add((entry.FullName, childChain), ct);
                                }
                            }
                            else
                            {
                                files++;
                            }

                            if (++pending == CountBatch)
                            {
                                Counters.Add(files, directories);
                                files = directories = 0;
                                pending = 0;
                                ct.ThrowIfCancellationRequested();
                            }
                        }

                        at.Listed(entries);
                    }
                    catch (OperationCanceledException ex)
                    {
                        at.Failed(ex, ct);
                        asked?.Failed(ex, ct);
                        throw;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        at.Failed(ex, ct);
                        asked?.Failed(ex, ct);
                        Logger.LogDebug("mirror: count skipped {Path}: {Error}", real, ex.Message);
                    }
                    finally
                    {
                        asked?.AddEntries(entries);
                        asked?.Done();

                        // the last folder listed: the other threads' waits for more end
                        if (Interlocked.Decrement(ref outstanding) == 0)
                        {
                            folders.CompleteAdding();
                        }
                    }
                }
            }
            finally
            {
                Counters.Add(files, directories);
            }
        }

        var counting = Enumerable.Range(0, threads)
            .Select(_ => Task.Factory.StartNew(Count, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default))
            .ToList();
        await Task.WhenAll(counting).ConfigureAwait(false);
    }

    protected override Task OnInitialiseAsync(CancellationToken ct)
    {
        _watcher = Context.UpstreamWatcher;
        var errors = new List<string>();
        foreach (var line in MirrorRoot.Lines(_settings.Paths))
        {
            if (!MirrorRoot.TryParse(line, OperatingSystem.IsWindows(), out var root, out var error))
            {
                errors.Add($"{line}: {error}");
                continue;
            }

            if (_roots.Any(r => r.ChainPath.Equals(root.ChainPath, StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add($"{line}: another path already appears at '{root.ChainPath}'");
                continue;
            }

            _roots.Add(root);
            var node = _root;
            foreach (var segment in root.Chain)
            {
                node = node.GetOrAdd(segment);
            }

            node.Root = root;
        }

        if (errors.Count > 0)
        {
            throw new SourceFailedException(string.Join("; ", errors), permanent: true);
        }

        if (_roots.Count == 0)
        {
            throw new SourceFailedException("no paths are configured", permanent: true);
        }

        Context.ReportProgress("Checking paths");
        CheckRootsCore(report: false);
        lock (_gate)
        {
            if (_unreachable.Count == _roots.Count)
            {
                throw new SourceFailedException(_roots.Count == 1 ? $"{_roots[0].Original} is not reachable" : $"none of the {_roots.Count} paths is reachable");
            }

            ReportHealthLocked();
        }

        _checker = Task.Run(() => CheckLoopAsync(_stop.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public override Task<VfsEntry?> StatAsync(string path, CancellationToken ct)
    {
        path = VfsPath.Normalize(path);
        var resolved = Resolve(path);
        if (resolved.Real is null)
        {
            return Task.FromResult<VfsEntry?>(resolved.Node is null ? null : SyntheticEntry(resolved.Node));
        }

        if (resolved.Node is null)
        {
            EnsureReachable(resolved.Root!);
        }

        try
        {
            var info = GetInfo(resolved.Real);
            if (info is null)
            {
                // a chain level is a folder even while the real folder behind it is missing
                return Task.FromResult(resolved.Node is { } node ? SyntheticEntry(node) : null);
            }

            return Task.FromResult<VfsEntry?>(ToEntry(info, path.Length == 0 ? "" : VfsPath.Name(path)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Translate(ex, path, resolved.Root);
        }
    }

    public override async IAsyncEnumerable<VfsEntry> ListAsync(string path, [EnumeratorCancellation] CancellationToken ct)
    {
        path = VfsPath.Normalize(path);
        var resolved = Resolve(path);
        if (resolved.Real is null && resolved.Node is null)
        {
            throw VfsException.NotFound(path);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (resolved.Real is not null)
        {
            EnsureReachable(resolved.Root!);
            List<VfsEntry> entries;
            try
            {
                var info = GetInfo(resolved.Real);
                if (info is null)
                {
                    if (resolved.Node is null)
                    {
                        throw VfsException.NotFound(path);
                    }

                    entries = [];
                }
                else if (info is not DirectoryInfo dir)
                {
                    throw VfsException.NotADirectory(path);
                }
                else
                {
                    entries = Listing(dir);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw Translate(ex, path, resolved.Root);
            }

            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();
                seen.Add(entry.Name);
                yield return entry;
            }
        }

        if (resolved.Node is not null)
        {
            foreach (var child in resolved.Node.Children.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (seen.Add(child.Name))
                {
                    yield return SyntheticEntry(child);
                }
            }
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public override Task<IVfsFile> OpenAsync(string path, VfsOpenMode mode, CancellationToken ct)
    {
        path = VfsPath.Normalize(path);
        var resolved = Resolve(path);
        if (resolved.Real is null)
        {
            throw resolved.Node is null ? VfsException.NotFound(path) : VfsException.IsADirectory(path);
        }

        if (resolved.Node is { } && IsFolder(resolved.Real))
        {
            throw VfsException.IsADirectory(path);
        }

        EnsureReachable(resolved.Root!);
        IWatchedRequest? asked = null;
        try
        {
            if (IsFolder(resolved.Real))
            {
                throw VfsException.IsADirectory(path);
            }

            var (fileMode, access) = mode switch
            {
                VfsOpenMode.Read => (FileMode.Open, FileAccess.Read),
                VfsOpenMode.ReadWrite => (FileMode.Open, FileAccess.ReadWrite),
                VfsOpenMode.CreateNew => (FileMode.CreateNew, FileAccess.ReadWrite),
                VfsOpenMode.Create => (FileMode.Create, FileAccess.ReadWrite),
                _ => (FileMode.OpenOrCreate, FileAccess.ReadWrite),
            };

            // the request stays open with the file, its bytes going by on it
            asked = _watcher?.Asked(access == FileAccess.Read ? "Read file" : "Write file", resolved.Real);
            var handle = File.OpenHandle(resolved.Real, fileMode, access, FileShare.ReadWrite | FileShare.Delete, FileOptions.Asynchronous);
            return Task.FromResult<IVfsFile>(new MirrorFile(handle, access != FileAccess.Read, asked));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            asked?.Failed(ex, ct);
            asked?.Done();
            throw Translate(ex, path, resolved.Root);
        }
    }

    public override Task CreateDirectoryAsync(string path, CancellationToken ct)
    {
        path = VfsPath.Normalize(path);
        var resolved = ResolveForWrite(path);
        try
        {
            if (IsFolder(resolved.Real) || IsFile(resolved.Real))
            {
                throw VfsException.AlreadyExists(path);
            }

            var parent = Path.GetDirectoryName(resolved.Real);
            if (parent is not null && !IsFolder(parent))
            {
                throw VfsException.NotFound(VfsPath.Parent(path));
            }

            _watcher.Ask("Create folder", resolved.Real, () => Directory.CreateDirectory(resolved.Real));
            return Task.CompletedTask;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Translate(ex, path, resolved.Root);
        }
    }

    public override Task DeleteAsync(string path, CancellationToken ct)
    {
        path = VfsPath.Normalize(path);
        var resolved = ResolveForWrite(path);
        try
        {
            if (IsFolder(resolved.Real))
            {
                if (_watcher.Ask(ListFolder, resolved.Real, () => Directory.EnumerateFileSystemEntries(resolved.Real).Any()))
                {
                    throw VfsException.DirectoryNotEmpty(path);
                }

                _watcher.Ask("Delete", resolved.Real, () => Directory.Delete(resolved.Real));
            }
            else if (IsFile(resolved.Real))
            {
                _watcher.Ask("Delete", resolved.Real, () => File.Delete(resolved.Real));
            }
            else
            {
                throw VfsException.NotFound(path);
            }

            return Task.CompletedTask;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Translate(ex, path, resolved.Root);
        }
    }

    public override Task RenameAsync(string fromPath, string toPath, bool replace, CancellationToken ct)
    {
        fromPath = VfsPath.Normalize(fromPath);
        toPath = VfsPath.Normalize(toPath);
        var from = ResolveForWrite(fromPath);
        var to = ResolveForWrite(toPath);
        try
        {
            var fromIsDir = IsFolder(from.Real);
            if (!fromIsDir && !IsFile(from.Real))
            {
                throw VfsException.NotFound(fromPath);
            }

            var toExists = IsFolder(to.Real) || IsFile(to.Real);
            if (toExists && (!replace || fromIsDir || IsFolder(to.Real)))
            {
                if (!string.Equals(from.Real, to.Real, StringComparison.OrdinalIgnoreCase))
                {
                    throw VfsException.AlreadyExists(toPath);
                }
            }

            _watcher.Ask("Rename", $"{from.Real} to {to.Real}", () =>
            {
                if (fromIsDir)
                {
                    Directory.Move(from.Real, to.Real);
                }
                else
                {
                    File.Move(from.Real, to.Real, overwrite: replace);
                }
            });
            return Task.CompletedTask;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Translate(ex, fromPath, from.Root);
        }
    }

    public override Task SetTimesAsync(string path, DateTimeOffset? created, DateTimeOffset? modified, CancellationToken ct)
    {
        path = VfsPath.Normalize(path);
        var resolved = ResolveForWrite(path);
        try
        {
            if (IsFolder(resolved.Real))
            {
                _watcher.Ask("Set times", resolved.Real, () =>
                {
                    if (created is not null)
                    {
                        Directory.SetCreationTimeUtc(resolved.Real, created.Value.UtcDateTime);
                    }

                    if (modified is not null)
                    {
                        Directory.SetLastWriteTimeUtc(resolved.Real, modified.Value.UtcDateTime);
                    }
                });
            }
            else if (IsFile(resolved.Real))
            {
                _watcher.Ask("Set times", resolved.Real, () =>
                {
                    if (created is not null)
                    {
                        File.SetCreationTimeUtc(resolved.Real, created.Value.UtcDateTime);
                    }

                    if (modified is not null)
                    {
                        File.SetLastWriteTimeUtc(resolved.Real, modified.Value.UtcDateTime);
                    }
                });
            }
            else
            {
                throw VfsException.NotFound(path);
            }

            return Task.CompletedTask;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Translate(ex, path, resolved.Root);
        }
    }

    /// <summary>
    /// The space of the volume a path is on. A folder of the chain above the mirrored paths has none of its own, so the
    /// first reachable path under it lends its own: a share of the mirror shows its disk's free space, not a made-up one.
    /// </summary>
    public override Task<VfsSpace?> GetSpaceAsync(string path, CancellationToken ct)
    {
        var resolved = Resolve(VfsPath.Normalize(path));
        var real = resolved.Real ?? (resolved.Node is { } node ? RootsUnder(node).FirstOrDefault(IsThere)?.FullPath : null);
        if (real is null)
        {
            return Task.FromResult<VfsSpace?>(null);
        }

        try
        {
            return Task.FromResult<VfsSpace?>(_watcher.Ask("Get space", real, () =>
            {
                var drive = new DriveInfo(real);
                return new VfsSpace(drive.TotalSize, drive.AvailableFreeSpace);
            }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Task.FromResult<VfsSpace?>(null);
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_checker is not null)
        {
            try
            {
                await _checker.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _stop.Dispose();
        await base.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>The deepest configured root along the path, the real path under it, and the chain node (if the path ends on one).</summary>
    internal Resolution Resolve(string normalizedPath)
    {
        var node = _root;
        MirrorRoot? root = null;
        var rootDepth = 0;
        var segments = VfsPath.Split(normalizedPath);
        var depth = 0;
        while (depth < segments.Length && node is not null && node.Children.TryGetValue(segments[depth], out var child))
        {
            node = child;
            depth++;
            if (node.Root is not null)
            {
                root = node.Root;
                rootDepth = depth;
            }
        }

        if (_root.Root is not null)
        {
            root ??= _root.Root;
        }

        var onNode = depth == segments.Length ? node : null;
        if (root is null)
        {
            return new Resolution(onNode, null, null);
        }

        var remaining = segments.Skip(rootDepth);
        var real = remaining.Any()
            ? Path.Combine(root.FullPath, string.Join(Path.DirectorySeparatorChar, remaining))
            : root.FullPath;
        return new Resolution(onNode, root, real);
    }

    private WriteResolution ResolveForWrite(string normalizedPath)
    {
        var resolved = Resolve(normalizedPath);
        if (resolved.Real is null)
        {
            if (resolved.Node is not null)
            {
                throw VfsException.ReadOnly(normalizedPath, "this folder is part of the mirror layout");
            }

            // a new name directly under a chain level (no configured path above it) can't be created either
            var parent = Resolve(VfsPath.Parent(normalizedPath));
            throw parent is { Node: not null, Real: null }
                ? VfsException.ReadOnly(normalizedPath, "this folder is part of the mirror layout")
                : VfsException.NotFound(normalizedPath);
        }

        if (resolved.Node is not null && resolved.Node.Root is null)
        {
            throw VfsException.ReadOnly(normalizedPath, "this folder is part of the mirror layout");
        }

        EnsureReachable(resolved.Root!);
        return new WriteResolution(resolved.Root!, resolved.Real);
    }

    /// <summary>Operations under a root that isn't there right now fail as Unavailable, not NotFound.</summary>
    private void EnsureReachable(MirrorRoot root)
    {
        bool unreachable;
        lock (_gate)
        {
            unreachable = _unreachable.Contains(root);
        }

        if (unreachable && IsThere(root))
        {
            CheckRootsCore(report: true);
            return;
        }

        if (unreachable || !IsThere(root))
        {
            MarkUnreachable(root);
            throw VfsException.Unavailable($"{root.Original} is not reachable");
        }
    }

    /// <summary>The check that a mirrored path is there, made for every call under it and every <see cref="MirrorSettings.CheckIntervalSeconds"/>.</summary>
    private bool IsThere(MirrorRoot root) => _watcher.Ask("Check path", root.FullPath, () => Directory.Exists(root.FullPath));

    private bool IsFolder(string real) => _watcher.Ask(GetMetadata, real, () => Directory.Exists(real));

    private bool IsFile(string real) => _watcher.Ask(GetMetadata, real, () => File.Exists(real));

    private FileSystemInfo? GetInfo(string real)
    {
        var asked = _watcher?.Asked(GetMetadata, real);
        try
        {
            var dir = new DirectoryInfo(real);
            if (dir.Exists)
            {
                return dir;
            }

            var file = new FileInfo(real);
            if (file.Exists)
            {
                return file;
            }

            asked?.Answered("not found", refused: false);
            return null;
        }
        catch (Exception ex)
        {
            asked?.Failed(ex, CancellationToken.None);
            throw;
        }
        finally
        {
            asked?.Done();
        }
    }

    /// <summary>A folder's entries from the file system.</summary>
    private List<VfsEntry> Listing(DirectoryInfo folder)
    {
        var asked = _watcher?.Asked(ListFolder, folder.FullName);
        try
        {
            var entries = folder.EnumerateFileSystemInfos("*", Enumeration).Select(i => ToEntry(i, i.Name)).ToList();
            asked?.AddEntries(entries.Count);
            return entries;
        }
        catch (Exception ex)
        {
            asked?.Failed(ex, CancellationToken.None);
            throw;
        }
        finally
        {
            asked?.Done();
        }
    }

    private static VfsEntry ToEntry(FileSystemInfo info, string name)
    {
        var attributes = VfsAttributes.None;
        if (info.Attributes.HasFlag(FileAttributes.Hidden))
        {
            attributes |= VfsAttributes.Hidden;
        }

        if (info.Attributes.HasFlag(FileAttributes.System))
        {
            attributes |= VfsAttributes.System;
        }

        if (info.Attributes.HasFlag(FileAttributes.ReadOnly))
        {
            attributes |= VfsAttributes.ReadOnly;
        }

        var isDir = info is DirectoryInfo || info.Attributes.HasFlag(FileAttributes.Directory);
        return new VfsEntry(
            name,
            isDir,
            isDir ? 0 : (info as FileInfo)?.Length ?? 0,
            new DateTimeOffset(info.CreationTimeUtc, TimeSpan.Zero),
            new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            attributes);
    }

    private static VfsEntry SyntheticEntry(Node node) => new(node.Name, true, 0, null, null, VfsAttributes.ReadOnly);

    private VfsException Translate(Exception ex, string path, MirrorRoot? root)
    {
        if (root is not null && ex is IOException && !IsThere(root))
        {
            MarkUnreachable(root);
            return VfsException.Unavailable($"{root.Original} is not reachable");
        }

        return VfsException.FromIo(ex, path);
    }

    private void MarkUnreachable(MirrorRoot root)
    {
        lock (_gate)
        {
            if (_unreachable.Add(root))
            {
                ReportHealthLocked();
            }
        }
    }

    private async Task CheckLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, _settings.CheckIntervalSeconds));
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(interval, ct).ConfigureAwait(false);
            CheckRootsCore(report: true);
        }
    }

    private void CheckRootsCore(bool report)
    {
        var unreachable = _roots.Where(r => !IsThere(r)).ToList();
        lock (_gate)
        {
            var changed = !_unreachable.SetEquals(unreachable);
            _unreachable.Clear();
            _unreachable.UnionWith(unreachable);
            if (report && changed)
            {
                ReportHealthLocked();
            }
        }
    }

    private void ReportHealthLocked()
    {
        if (!IsInitialised)
        {
            return;
        }

        if (_unreachable.Count == 0)
        {
            Context.ReportHealth(ComponentHealth.Ready);
        }
        else if (_unreachable.Count == _roots.Count)
        {
            Context.ReportHealth(ComponentHealth.Failed, _roots.Count == 1 ? $"{_roots[0].Original} is not reachable" : "none of the paths is reachable");
        }
        else
        {
            Context.ReportHealth(ComponentHealth.Degraded, "unreachable: " + string.Join(", ", _unreachable.Select(r => r.Original)));
        }

        Logger.LogInformation("mirror: {Reachable} of {Total} paths reachable", _roots.Count - _unreachable.Count, _roots.Count);
    }

    /// <summary>The mirrored paths at or under a folder of the chain, in name order.</summary>
    private static IEnumerable<MirrorRoot> RootsUnder(Node node)
    {
        if (node.Root is not null)
        {
            yield return node.Root;
        }

        foreach (var child in node.Children.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var root in RootsUnder(child))
            {
                yield return root;
            }
        }
    }

    internal sealed record Resolution(Node? Node, MirrorRoot? Root, string? Real);

    private sealed record WriteResolution(MirrorRoot Root, string Real);

    internal sealed class Node(string name)
    {
        public string Name { get; } = name;

        public MirrorRoot? Root { get; set; }

        public Dictionary<string, Node> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Node GetOrAdd(string segment)
        {
            if (!Children.TryGetValue(segment, out var child))
            {
                child = new Node(segment);
                Children[segment] = child;
            }

            return child;
        }
    }

    /// <param name="asked">The request this open file is, its bytes going by on it until it is closed.</param>
    private sealed class MirrorFile(SafeFileHandle handle, bool writable, IWatchedRequest? asked) : IVfsFile
    {
        public long Length => RandomAccess.GetLength(handle);

        public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct)
        {
            if (offset < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }

            return asked is null ? RandomAccess.ReadAsync(handle, buffer, offset, ct) : WatchedReadAsync(offset, buffer, asked, ct);
        }

        private async ValueTask<int> WatchedReadAsync(long offset, Memory<byte> buffer, IWatchedRequest request, CancellationToken ct)
        {
            try
            {
                var read = await RandomAccess.ReadAsync(handle, buffer, offset, ct).ConfigureAwait(false);
                request.AddBytes(read);
                return read;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                request.Failed(ex, ct);
                throw;
            }
        }

        public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            if (!writable)
            {
                throw VfsException.NotSupported("writing to a file opened for reading");
            }

            return asked is null ? RandomAccess.WriteAsync(handle, data, offset, ct) : WatchedWriteAsync(offset, data, asked, ct);
        }

        private async ValueTask WatchedWriteAsync(long offset, ReadOnlyMemory<byte> data, IWatchedRequest request, CancellationToken ct)
        {
            try
            {
                await RandomAccess.WriteAsync(handle, data, offset, ct).ConfigureAwait(false);
                request.AddBytes(data.Length);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                request.Failed(ex, ct);
                throw;
            }
        }

        public ValueTask SetLengthAsync(long length, CancellationToken ct)
        {
            if (!writable)
            {
                throw VfsException.NotSupported("writing to a file opened for reading");
            }

            RandomAccess.SetLength(handle, length);
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushAsync(CancellationToken ct)
        {
            if (writable)
            {
                RandomAccess.FlushToDisk(handle);
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            handle.Dispose();
            asked?.Done();
            return ValueTask.CompletedTask;
        }
    }
}
