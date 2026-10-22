using AnyFS.Sdk;

namespace AnyFS.Sources.Common;

/// <summary>A file a source makes itself and holds in memory, read-only: a page as Markdown, a link, a metadata file.</summary>
/// <param name="what">What it is, for the refusal to write ("a page").</param>
public sealed class MemoryFile(byte[] content, string what = "a file the source makes") : IVfsFile
{
    public long Length => content.Length;

    public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct)
    {
        if (offset >= content.Length)
        {
            return ValueTask.FromResult(0);
        }

        var n = (int)Math.Min(buffer.Length, content.Length - offset);
        content.AsMemory((int)offset, n).CopyTo(buffer);
        return ValueTask.FromResult(n);
    }

    public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct) => throw VfsException.NotSupported($"writing {what}");

    public ValueTask SetLengthAsync(long length, CancellationToken ct) => throw VfsException.NotSupported($"writing {what}");

    public ValueTask FlushAsync(CancellationToken ct) => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
