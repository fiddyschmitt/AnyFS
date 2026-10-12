namespace AnyFS.Sdk.Testing;

/// <summary>
/// One source presented as a whole collection, for testing a mount provider without the AnyFS host:
/// the source's root is the collection root; <paramref name="readOnly"/> plays the mount's flag.
/// </summary>
public sealed class SingleSourceVfs(ISource source, string collectionName = "test", bool readOnly = false) : IVirtualFileSystem
{
    public string CollectionName => collectionName;

    public bool ReadOnly => readOnly;

    public bool CanWrite(string path) => !readOnly && source.Capabilities.CanWrite;

    public Task<VfsEntry?> StatAsync(string path, CancellationToken ct) => source.StatAsync(path, ct);

    public IAsyncEnumerable<VfsEntry> ListAsync(string path, CancellationToken ct) => source.ListAsync(path, ct);

    public Task<IVfsFile> OpenAsync(string path, VfsOpenMode mode, CancellationToken ct)
        => mode.IsWrite() && readOnly ? throw VfsException.ReadOnly(path, "the mount is read-only") : source.OpenAsync(path, mode, ct);

    public Task CreateDirectoryAsync(string path, CancellationToken ct)
        => readOnly ? throw VfsException.ReadOnly(path, "the mount is read-only") : source.CreateDirectoryAsync(path, ct);

    public Task DeleteAsync(string path, CancellationToken ct)
        => readOnly ? throw VfsException.ReadOnly(path, "the mount is read-only") : source.DeleteAsync(path, ct);

    public Task RenameAsync(string fromPath, string toPath, bool replace, CancellationToken ct)
        => readOnly ? throw VfsException.ReadOnly(fromPath, "the mount is read-only") : source.RenameAsync(fromPath, toPath, replace, ct);

    public Task SetTimesAsync(string path, DateTimeOffset? created, DateTimeOffset? modified, CancellationToken ct)
        => readOnly ? throw VfsException.ReadOnly(path, "the mount is read-only") : source.SetTimesAsync(path, created, modified, ct);

    public Task<VfsSpace?> GetSpaceAsync(string path, CancellationToken ct) => source.GetSpaceAsync(path, ct);
}
