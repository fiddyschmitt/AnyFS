using System.Collections.Concurrent;
using AnyFS.Sdk;

namespace AnyFS.Mounts.Tests;

/// <summary>A file system that tells how each file opened in it was closed: "closed", which keeps what was written, or "given up".</summary>
internal sealed class ClosesTold(IVirtualFileSystem inner) : IVirtualFileSystem
{
    private readonly ConcurrentQueue<(string Path, string How)> _closes = new();

    public string[] Of(string path) => _closes.Where(c => string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase)).Select(c => c.How).ToArray();

    public string CollectionName => inner.CollectionName;

    public bool ReadOnly => inner.ReadOnly;

    public bool CanWrite(string path) => inner.CanWrite(path);

    public Task<VfsEntry?> StatAsync(string path, CancellationToken ct) => inner.StatAsync(path, ct);

    public IAsyncEnumerable<VfsEntry> ListAsync(string path, CancellationToken ct) => inner.ListAsync(path, ct);

    public async Task<IVfsFile> OpenAsync(string path, VfsOpenMode mode, CancellationToken ct) => new Told(await inner.OpenAsync(path, mode, ct), VfsPath.Normalize(path), _closes);

    public Task CreateDirectoryAsync(string path, CancellationToken ct) => inner.CreateDirectoryAsync(path, ct);

    public Task DeleteAsync(string path, CancellationToken ct) => inner.DeleteAsync(path, ct);

    public Task RenameAsync(string fromPath, string toPath, bool replace, CancellationToken ct) => inner.RenameAsync(fromPath, toPath, replace, ct);

    public Task SetTimesAsync(string path, DateTimeOffset? created, DateTimeOffset? modified, CancellationToken ct) => inner.SetTimesAsync(path, created, modified, ct);

    public Task<VfsSpace?> GetSpaceAsync(string path, CancellationToken ct) => inner.GetSpaceAsync(path, ct);

    private sealed class Told(IVfsFile inner, string path, ConcurrentQueue<(string Path, string How)> closes) : IVfsFile
    {
        public long Length => inner.Length;

        public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct) => inner.ReadAsync(offset, buffer, ct);

        public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct) => inner.WriteAsync(offset, data, ct);

        public ValueTask SetLengthAsync(long length, CancellationToken ct) => inner.SetLengthAsync(length, ct);

        public ValueTask FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);

        public ValueTask AbandonAsync()
        {
            closes.Enqueue((path, "given up"));
            return inner.AbandonAsync();
        }

        public ValueTask DisposeAsync()
        {
            closes.Enqueue((path, "closed"));
            return inner.DisposeAsync();
        }
    }
}
