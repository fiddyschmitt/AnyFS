using System.Runtime.CompilerServices;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace AnyFS.Sources.Mirror;

/// <summary>
/// System.IO throughout: FileStream-free ranged reads through <see cref="RandomAccess"/> on handles opened
/// with FileShare.ReadWrite | Delete. Status: Ready when every root is reachable, Degraded naming the ones
/// that aren't, Failed only when none is. Writes go through when the collection and mount allow them.
/// </summary>
public sealed class MirrorSource : SourceBase
{
    private static readonly EnumerationOptions Enumeration = new() { AttributesToSkip = 0, IgnoreInaccessible = true, ReturnSpecialDirectories = false };

    private readonly MirrorSettings _settings;
    private readonly Node _root = new("");
    private readonly List<MirrorRoot> _roots = [];
    private readonly HashSet<MirrorRoot> _unreachable = [];
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _checker;

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

    protected override Task OnInitialiseAsync(CancellationToken ct)
    {
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
                    entries = dir.EnumerateFileSystemInfos("*", Enumeration).Select(i => ToEntry(i, i.Name)).ToList();
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

        if (resolved.Node is { } && Directory.Exists(resolved.Real))
        {
            throw VfsException.IsADirectory(path);
        }

        EnsureReachable(resolved.Root!);
        try
        {
            if (Directory.Exists(resolved.Real))
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
            var handle = File.OpenHandle(resolved.Real, fileMode, access, FileShare.ReadWrite | FileShare.Delete, FileOptions.Asynchronous);
            return Task.FromResult<IVfsFile>(new MirrorFile(handle, access != FileAccess.Read));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Translate(ex, path, resolved.Root);
        }
    }

    public override Task CreateDirectoryAsync(string path, CancellationToken ct)
    {
        path = VfsPath.Normalize(path);
        var resolved = ResolveForWrite(path);
        try
        {
            if (Directory.Exists(resolved.Real) || File.Exists(resolved.Real))
            {
                throw VfsException.AlreadyExists(path);
            }

            var parent = Path.GetDirectoryName(resolved.Real);
            if (parent is not null && !Directory.Exists(parent))
            {
                throw VfsException.NotFound(VfsPath.Parent(path));
            }

            Directory.CreateDirectory(resolved.Real);
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
            if (Directory.Exists(resolved.Real))
            {
                if (Directory.EnumerateFileSystemEntries(resolved.Real).Any())
                {
                    throw VfsException.DirectoryNotEmpty(path);
                }

                Directory.Delete(resolved.Real);
            }
            else if (File.Exists(resolved.Real))
            {
                File.Delete(resolved.Real);
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
            var fromIsDir = Directory.Exists(from.Real);
            if (!fromIsDir && !File.Exists(from.Real))
            {
                throw VfsException.NotFound(fromPath);
            }

            var toExists = Directory.Exists(to.Real) || File.Exists(to.Real);
            if (toExists && (!replace || fromIsDir || Directory.Exists(to.Real)))
            {
                if (!string.Equals(from.Real, to.Real, StringComparison.OrdinalIgnoreCase))
                {
                    throw VfsException.AlreadyExists(toPath);
                }
            }

            if (fromIsDir)
            {
                Directory.Move(from.Real, to.Real);
            }
            else
            {
                File.Move(from.Real, to.Real, overwrite: replace);
            }

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
            if (Directory.Exists(resolved.Real))
            {
                if (created is not null)
                {
                    Directory.SetCreationTimeUtc(resolved.Real, created.Value.UtcDateTime);
                }

                if (modified is not null)
                {
                    Directory.SetLastWriteTimeUtc(resolved.Real, modified.Value.UtcDateTime);
                }
            }
            else if (File.Exists(resolved.Real))
            {
                if (created is not null)
                {
                    File.SetCreationTimeUtc(resolved.Real, created.Value.UtcDateTime);
                }

                if (modified is not null)
                {
                    File.SetLastWriteTimeUtc(resolved.Real, modified.Value.UtcDateTime);
                }
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

    public override Task<VfsSpace?> GetSpaceAsync(string path, CancellationToken ct)
    {
        var resolved = Resolve(VfsPath.Normalize(path));
        if (resolved.Real is null)
        {
            return Task.FromResult<VfsSpace?>(null);
        }

        try
        {
            var drive = new DriveInfo(resolved.Real);
            return Task.FromResult<VfsSpace?>(new VfsSpace(drive.TotalSize, drive.AvailableFreeSpace));
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

        if (unreachable && Directory.Exists(root.FullPath))
        {
            CheckRootsCore(report: true);
            return;
        }

        if (unreachable || !Directory.Exists(root.FullPath))
        {
            MarkUnreachable(root);
            throw VfsException.Unavailable($"{root.Original} is not reachable");
        }
    }

    private static FileSystemInfo? GetInfo(string real)
    {
        var dir = new DirectoryInfo(real);
        if (dir.Exists)
        {
            return dir;
        }

        var file = new FileInfo(real);
        return file.Exists ? file : null;
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
        if (root is not null && ex is IOException && !Directory.Exists(root.FullPath))
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
        var unreachable = _roots.Where(r => !Directory.Exists(r.FullPath)).ToList();
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

    private sealed class MirrorFile(SafeFileHandle handle, bool writable) : IVfsFile
    {
        public long Length => RandomAccess.GetLength(handle);

        public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct)
        {
            if (offset < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }

            return RandomAccess.ReadAsync(handle, buffer, offset, ct);
        }

        public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            if (!writable)
            {
                throw VfsException.NotSupported("writing to a file opened for reading");
            }

            return RandomAccess.WriteAsync(handle, data, offset, ct);
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
            return ValueTask.CompletedTask;
        }
    }
}
