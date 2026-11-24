using AnyFS.Sdk;

namespace AnyFS.Mounts.Tests;

/// <summary>
/// A file system in which a test makes things go wrong, the way a source's own fault reaches a mount. The test is asked at
/// each "stat" of a path, at each "read" of a file with the read's offset, and at a file's "close": what it gives is
/// thrown in place of the call (a close is made first), and with nothing given the call goes through.
/// </summary>
internal sealed class Faulty(IVirtualFileSystem inner, Func<string, string, long, Exception?> fault) : IVirtualFileSystem
{
    public string CollectionName => inner.CollectionName;

    public bool ReadOnly => inner.ReadOnly;

    public bool CanWrite(string path) => inner.CanWrite(path);

    public Task<VfsEntry?> StatAsync(string path, CancellationToken ct)
        => fault("stat", VfsPath.Normalize(path), 0) is { } thrown ? throw thrown : inner.StatAsync(path, ct);

    public IAsyncEnumerable<VfsEntry> ListAsync(string path, CancellationToken ct) => inner.ListAsync(path, ct);

    public async Task<IVfsFile> OpenAsync(string path, VfsOpenMode mode, CancellationToken ct) => new File(await inner.OpenAsync(path, mode, ct), VfsPath.Normalize(path), fault);

    public Task CreateDirectoryAsync(string path, CancellationToken ct) => inner.CreateDirectoryAsync(path, ct);

    public Task DeleteAsync(string path, CancellationToken ct) => inner.DeleteAsync(path, ct);

    public Task RenameAsync(string fromPath, string toPath, bool replace, CancellationToken ct) => inner.RenameAsync(fromPath, toPath, replace, ct);

    public Task SetTimesAsync(string path, DateTimeOffset? created, DateTimeOffset? modified, CancellationToken ct) => inner.SetTimesAsync(path, created, modified, ct);

    public Task<VfsSpace?> GetSpaceAsync(string path, CancellationToken ct) => inner.GetSpaceAsync(path, ct);

    private sealed class File(IVfsFile inner, string path, Func<string, string, long, Exception?> fault) : IVfsFile
    {
        public long Length => inner.Length;

        public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct)
            => fault("read", path, offset) is { } thrown ? throw thrown : inner.ReadAsync(offset, buffer, ct);

        public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct) => inner.WriteAsync(offset, data, ct);

        public ValueTask SetLengthAsync(long length, CancellationToken ct) => inner.SetLengthAsync(length, ct);

        public ValueTask FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);

        public ValueTask AbandonAsync() => inner.AbandonAsync();

        public async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            if (fault("close", path, 0) is { } thrown)
            {
                throw thrown;
            }
        }
    }
}
