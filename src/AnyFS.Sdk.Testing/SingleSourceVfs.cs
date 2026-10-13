namespace AnyFS.Sdk.Testing;

/// <summary>
/// One source presented as a whole collection, for testing a mount provider without the AnyFS host:
/// the source's root is the collection root; <paramref name="readOnly"/> plays the mount's flag, and the
/// source's own <see cref="SourceCapabilities.CanWrite"/> plays the capability layer, so writes are refused
/// the way the real router refuses them.
/// </summary>
public sealed class SingleSourceVfs(ISource source, string collectionName = "test", bool readOnly = false) : IVirtualFileSystem
{
    public string CollectionName => collectionName;

    public bool ReadOnly => readOnly;

    public bool CanWrite(string path) => !readOnly && source.Capabilities.CanWrite;

    public Task<VfsEntry?> StatAsync(string path, CancellationToken ct) => source.StatAsync(path, ct);

    public IAsyncEnumerable<VfsEntry> ListAsync(string path, CancellationToken ct) => source.ListAsync(path, ct);

    public Task<IVfsFile> OpenAsync(string path, VfsOpenMode mode, CancellationToken ct)
        => mode.IsWrite() && !CanWrite(path) ? throw Refuse(path) : source.OpenAsync(path, mode, ct);

    public Task CreateDirectoryAsync(string path, CancellationToken ct)
        => !CanWrite(path) ? throw Refuse(path) : source.CreateDirectoryAsync(path, ct);

    public Task DeleteAsync(string path, CancellationToken ct)
        => !CanWrite(path) ? throw Refuse(path) : source.DeleteAsync(path, ct);

    public Task RenameAsync(string fromPath, string toPath, bool replace, CancellationToken ct)
        => !CanWrite(fromPath) ? throw Refuse(fromPath) : source.RenameAsync(fromPath, toPath, replace, ct);

    public Task SetTimesAsync(string path, DateTimeOffset? created, DateTimeOffset? modified, CancellationToken ct)
        => !CanWrite(path) ? throw Refuse(path) : source.SetTimesAsync(path, created, modified, ct);

    public Task<VfsSpace?> GetSpaceAsync(string path, CancellationToken ct) => source.GetSpaceAsync(path, ct);

    private VfsException Refuse(string path) => VfsException.ReadOnly(path, readOnly ? "the mount is read-only" : "the source can't write");
}
