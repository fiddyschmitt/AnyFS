using System.Runtime.CompilerServices;
using System.Text;

namespace AnyFS.Sdk.Testing;

/// <summary>
/// An in-memory source with a tree you build in the test. Used by AnyFS's own tests as the stand-in for
/// any source; plugin authors can use it to test code that consumes an <see cref="ISource"/>.
/// </summary>
public sealed class MemorySource : SourceBase
{
    private readonly Node _root;
    private readonly object _gate = new();
    private readonly SourceCapabilities _capabilities;

    public MemorySource(bool canWrite = true, bool caseInsensitive = false)
    {
        _capabilities = new SourceCapabilities(canWrite, caseInsensitive);
        _root = new Node("", true, caseInsensitive);
    }

    public override SourceCapabilities Capabilities => _capabilities;

    /// <summary>Hook for tests of the host: runs inside <see cref="SourceBase.OnInitialiseAsync"/>.</summary>
    public Func<ISourceContext, CancellationToken, Task>? OnInitialise { get; set; }

    /// <summary>Hook for tests of the host: runs first in <see cref="DisposeAsync"/>, with the context the source was given (null if it never was).</summary>
    public Func<ISourceContext?, Task>? OnDispose { get; set; }

    public int InitialiseCalls { get; private set; }

    public int ListCalls { get; private set; }

    public bool Disposed { get; private set; }

    public ISourceContext? LastContext { get; private set; }

    public MemorySource AddDirectory(string path)
    {
        lock (_gate)
        {
            GetOrCreateDirectory(VfsPath.Normalize(path));
        }

        return this;
    }

    public MemorySource AddFile(string path, string text) => AddFile(path, Encoding.UTF8.GetBytes(text));

    public MemorySource AddFile(string path, byte[] bytes, DateTimeOffset? modified = null)
    {
        path = VfsPath.Normalize(path);
        lock (_gate)
        {
            var (parent, name) = VfsPath.SplitLast(path);
            var dir = GetOrCreateDirectory(parent);
            var file = new Node(name, false, _capabilities.CaseInsensitive) { Data = bytes.ToArray(), Length = bytes.Length };
            if (modified is not null)
            {
                file.Modified = modified.Value;
            }

            dir.Children[name] = file;
        }

        return this;
    }

    /// <summary>The bytes of a file, for asserting what a write did.</summary>
    public byte[]? GetFile(string path)
    {
        lock (_gate)
        {
            var node = Find(VfsPath.Normalize(path));
            return node is { IsDirectory: false } ? node.Data.AsSpan(0, (int)node.Length).ToArray() : null;
        }
    }

    public bool Exists(string path)
    {
        lock (_gate)
        {
            return Find(VfsPath.Normalize(path)) is not null;
        }
    }

    protected override async Task OnInitialiseAsync(CancellationToken ct)
    {
        InitialiseCalls++;
        LastContext = Context;
        if (OnInitialise is not null)
        {
            await OnInitialise(Context, ct).ConfigureAwait(false);
        }
    }

    public override Task<VfsEntry?> StatAsync(string path, CancellationToken ct)
    {
        path = VfsPath.Normalize(path);
        lock (_gate)
        {
            return Task.FromResult(Find(path)?.ToEntry());
        }
    }

    public override async IAsyncEnumerable<VfsEntry> ListAsync(string path, [EnumeratorCancellation] CancellationToken ct)
    {
        path = VfsPath.Normalize(path);
        List<VfsEntry> entries;
        lock (_gate)
        {
            ListCalls++;
            var node = Find(path) ?? throw VfsException.NotFound(path);
            if (!node.IsDirectory)
            {
                throw VfsException.NotADirectory(path);
            }

            entries = node.Children.Values.OrderBy(n => n.Name, StringComparer.Ordinal).Select(n => n.ToEntry()).ToList();
        }

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return entry;
        }
    }

    public override Task<IVfsFile> OpenAsync(string path, VfsOpenMode mode, CancellationToken ct)
    {
        path = VfsPath.Normalize(path);
        if (mode.IsWrite())
        {
            RequireWrite("writing");
        }

        lock (_gate)
        {
            var node = Find(path);
            if (node is { IsDirectory: true })
            {
                throw VfsException.IsADirectory(path);
            }

            switch (mode)
            {
                case VfsOpenMode.Read or VfsOpenMode.ReadWrite when node is null:
                    throw VfsException.NotFound(path);
                case VfsOpenMode.CreateNew when node is not null:
                    throw VfsException.AlreadyExists(path);
            }

            if (node is null || mode == VfsOpenMode.Create)
            {
                var (parent, name) = VfsPath.SplitLast(path);
                var dir = Find(parent) ?? throw VfsException.NotFound(parent);
                if (!dir.IsDirectory)
                {
                    throw VfsException.NotADirectory(parent);
                }

                node = new Node(name, false, _capabilities.CaseInsensitive);
                dir.Children[name] = node;
            }

            return Task.FromResult<IVfsFile>(new MemoryFile(node, _gate, mode.IsWrite()));
        }
    }

    public override Task CreateDirectoryAsync(string path, CancellationToken ct)
    {
        RequireWrite("creating directories");
        path = VfsPath.Normalize(path);
        lock (_gate)
        {
            if (Find(path) is not null)
            {
                throw VfsException.AlreadyExists(path);
            }

            var (parent, name) = VfsPath.SplitLast(path);
            var dir = Find(parent) ?? throw VfsException.NotFound(parent);
            if (!dir.IsDirectory)
            {
                throw VfsException.NotADirectory(parent);
            }

            dir.Children[name] = new Node(name, true, _capabilities.CaseInsensitive);
        }

        return Task.CompletedTask;
    }

    public override Task DeleteAsync(string path, CancellationToken ct)
    {
        RequireWrite("deleting");
        path = VfsPath.Normalize(path);
        lock (_gate)
        {
            var node = Find(path) ?? throw VfsException.NotFound(path);
            if (node.IsDirectory && node.Children.Count > 0)
            {
                throw VfsException.DirectoryNotEmpty(path);
            }

            var (parent, name) = VfsPath.SplitLast(path);
            Find(parent)!.Children.Remove(name);
        }

        return Task.CompletedTask;
    }

    public override Task RenameAsync(string fromPath, string toPath, bool replace, CancellationToken ct)
    {
        RequireWrite("renaming");
        fromPath = VfsPath.Normalize(fromPath);
        toPath = VfsPath.Normalize(toPath);
        lock (_gate)
        {
            var node = Find(fromPath) ?? throw VfsException.NotFound(fromPath);
            var existing = Find(toPath);

            // the node itself under another case (a case-insensitive source) is a case-only rename, not a collision
            if (existing is not null && !ReferenceEquals(existing, node))
            {
                if (!replace || existing.IsDirectory)
                {
                    throw VfsException.AlreadyExists(toPath);
                }
            }

            var (toParent, toName) = VfsPath.SplitLast(toPath);
            var dir = Find(toParent) ?? throw VfsException.NotFound(toParent);
            var (fromParent, fromName) = VfsPath.SplitLast(fromPath);
            Find(fromParent)!.Children.Remove(fromName);
            node.Name = toName;
            dir.Children[toName] = node;
        }

        return Task.CompletedTask;
    }

    public override Task SetTimesAsync(string path, DateTimeOffset? created, DateTimeOffset? modified, CancellationToken ct)
    {
        RequireWrite("setting times");
        path = VfsPath.Normalize(path);
        lock (_gate)
        {
            var node = Find(path) ?? throw VfsException.NotFound(path);
            if (created is not null)
            {
                node.Created = created.Value;
            }

            if (modified is not null)
            {
                node.Modified = modified.Value;
            }
        }

        return Task.CompletedTask;
    }

    public override async ValueTask DisposeAsync()
    {
        if (OnDispose is not null)
        {
            await OnDispose(LastContext).ConfigureAwait(false);
        }

        Disposed = true;
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private Node? Find(string normalizedPath)
    {
        var node = _root;
        foreach (var seg in VfsPath.Split(normalizedPath))
        {
            if (!node.IsDirectory || !node.Children.TryGetValue(seg, out var child))
            {
                return null;
            }

            node = child;
        }

        return node;
    }

    private Node GetOrCreateDirectory(string normalizedPath)
    {
        var node = _root;
        foreach (var seg in VfsPath.Split(normalizedPath))
        {
            if (!node.Children.TryGetValue(seg, out var child))
            {
                child = new Node(seg, true, _capabilities.CaseInsensitive);
                node.Children[seg] = child;
            }

            node = child;
        }

        return node;
    }

    private sealed class Node(string name, bool isDirectory, bool caseInsensitive)
    {
        public string Name { get; set; } = name;

        public bool IsDirectory { get; } = isDirectory;

        public byte[] Data = [];

        public long Length { get; set; }

        public DateTimeOffset Created { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public DateTimeOffset Modified { get; set; } = new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);

        public Dictionary<string, Node> Children { get; } = new(caseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        public VfsEntry ToEntry() => new(Name, IsDirectory, IsDirectory ? 0 : Length, Created, Modified);
    }

    private sealed class MemoryFile(Node node, object gate, bool writable) : IVfsFile
    {
        public long Length
        {
            get
            {
                lock (gate)
                {
                    return node.Length;
                }
            }
        }

        public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct)
        {
            lock (gate)
            {
                if (offset >= node.Length)
                {
                    return ValueTask.FromResult(0);
                }

                var n = (int)Math.Min(buffer.Length, node.Length - offset);
                node.Data.AsSpan((int)offset, n).CopyTo(buffer.Span);
                return ValueTask.FromResult(n);
            }
        }

        public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            if (!writable)
            {
                throw VfsException.NotSupported("writing");
            }

            lock (gate)
            {
                var end = offset + data.Length;
                if (end > node.Data.Length)
                {
                    Array.Resize(ref node.Data, (int)Math.Max(end, node.Data.Length * 2L));
                }

                data.Span.CopyTo(node.Data.AsSpan((int)offset));
                node.Length = Math.Max(node.Length, end);
                node.Modified = DateTimeOffset.UtcNow;
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask SetLengthAsync(long length, CancellationToken ct)
        {
            if (!writable)
            {
                throw VfsException.NotSupported("writing");
            }

            lock (gate)
            {
                if (length > node.Data.Length)
                {
                    Array.Resize(ref node.Data, (int)length);
                }
                else if (length < node.Length)
                {
                    node.Data.AsSpan((int)length).Clear();
                }

                node.Length = length;
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask FlushAsync(CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
