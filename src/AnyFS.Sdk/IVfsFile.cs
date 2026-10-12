namespace AnyFS.Sdk;

public enum VfsOpenMode
{
    /// <summary>Open an existing file for reading.</summary>
    Read,
    /// <summary>Open an existing file for reading and writing.</summary>
    ReadWrite,
    /// <summary>Create a new file; fail if it exists.</summary>
    CreateNew,
    /// <summary>Create a new file or truncate an existing one.</summary>
    Create,
    /// <summary>Open for reading and writing, creating the file if it doesn't exist.</summary>
    OpenOrCreate,
}

public static class VfsOpenModeExtensions
{
    public static bool IsWrite(this VfsOpenMode mode) => mode != VfsOpenMode.Read;

    public static bool MayCreate(this VfsOpenMode mode) => mode is VfsOpenMode.CreateNew or VfsOpenMode.Create or VfsOpenMode.OpenOrCreate;
}

/// <summary>
/// An open file. Positionless: every call carries its offset, because that's what WinFsp, FUSE, NFS and
/// SMB ask for. Calls may arrive concurrently from several threads.
/// </summary>
public interface IVfsFile : IAsyncDisposable
{
    long Length { get; }

    /// <summary>Reads up to <paramref name="buffer"/>.Length bytes at <paramref name="offset"/>; returns 0 at or past the end.</summary>
    ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct);

    ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct);

    ValueTask SetLengthAsync(long length, CancellationToken ct);

    ValueTask FlushAsync(CancellationToken ct);
}

public static class VfsFileExtensions
{
    /// <summary>Reads the whole range or until the end of the file, looping over short reads.</summary>
    public static async ValueTask<int> ReadFullyAsync(this IVfsFile file, long offset, Memory<byte> buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await file.ReadAsync(offset + total, buffer[total..], ct).ConfigureAwait(false);
            if (n <= 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }
}
