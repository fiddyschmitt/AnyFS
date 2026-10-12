namespace AnyFS.Sdk;

/// <summary>
/// The operation set shared by sources (over their own tree) and by the virtual file system a mount
/// receives (over a whole collection). Paths are relative, '/'-separated, without a leading slash;
/// the root is the empty string. The host normalises paths before they reach a source.
/// </summary>
public interface IVfsOperations
{
    Task<VfsEntry?> StatAsync(string path, CancellationToken ct);

    /// <summary>Lists a directory. Throws <see cref="VfsError.NotFound"/> or <see cref="VfsError.NotADirectory"/>.</summary>
    IAsyncEnumerable<VfsEntry> ListAsync(string path, CancellationToken ct);

    Task<IVfsFile> OpenAsync(string path, VfsOpenMode mode, CancellationToken ct);

    Task CreateDirectoryAsync(string path, CancellationToken ct);

    /// <summary>Deletes a file or an empty directory.</summary>
    Task DeleteAsync(string path, CancellationToken ct);

    Task RenameAsync(string fromPath, string toPath, bool replace, CancellationToken ct);

    Task SetTimesAsync(string path, DateTimeOffset? created, DateTimeOffset? modified, CancellationToken ct);

    /// <summary>Space on the volume holding <paramref name="path"/>, or null when unknown.</summary>
    Task<VfsSpace?> GetSpaceAsync(string path, CancellationToken ct);
}
