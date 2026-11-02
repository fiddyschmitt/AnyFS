using System.Runtime.CompilerServices;
using AnyFS.Core.Runtime;
using AnyFS.Sdk;

namespace AnyFS.Core.Operations;

/// <summary>
/// The source as its runner hands it out (<see cref="SourceHandle.Current"/>): every call goes through here and is
/// recorded in the <see cref="OperationTracker"/>, so the router, the count walk and anything else that reaches a source
/// through its handle is seen in one place. A file handle is one operation from the open until it is closed, with the
/// bytes that went through it; a listing is one from its first entry to its last.
/// </summary>
public sealed class TrackedSource : ISource
{
    private readonly SourceHandle _handle;
    private readonly OperationTracker _tracker;

    public TrackedSource(ISource inner, SourceHandle handle, OperationTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(tracker);
        Inner = inner;
        _handle = handle;
        _tracker = tracker;
    }

    /// <summary>The source itself.</summary>
    public ISource Inner { get; }

    /// <summary>The source itself, whether or not it is tracked.</summary>
    public static ISource Unwrap(ISource source) => source is TrackedSource tracked ? tracked.Inner : source;

    public SourceCapabilities Capabilities => Inner.Capabilities;

    public SourceCounters Counters => Inner.Counters;

    /// <summary>Begins an operation of this source, named as the source is now.</summary>
    public Operation Begin(OperationKind kind, string path = "", string? detail = null) => _tracker.Begin(_handle.Id, _handle.Name, kind, path, detail);

    public Task InitialiseAsync(ISourceContext context, CancellationToken ct)
        => TrackAsync(Begin(OperationKind.Start), () => Inner.InitialiseAsync(context, ct), ct);

    public async Task<VfsEntry?> StatAsync(string path, CancellationToken ct)
    {
        var op = Begin(OperationKind.Stat, path);
        try
        {
            var entry = await Inner.StatAsync(path, ct).ConfigureAwait(false);
            if (entry is null)
            {
                op.Detail = "not found";
            }

            op.Complete();
            return entry;
        }
        catch (Exception ex)
        {
            op.Fail(ex, ct);
            throw;
        }
    }

    public async IAsyncEnumerable<VfsEntry> ListAsync(string path, [EnumeratorCancellation] CancellationToken ct)
    {
        var op = Begin(OperationKind.List, path);
        IAsyncEnumerator<VfsEntry> enumerator;
        try
        {
            enumerator = Inner.ListAsync(path, ct).GetAsyncEnumerator(ct);
        }
        catch (Exception ex)
        {
            // a source whose ListAsync throws before it gets to an iterator of its own: left out, the row would run forever
            op.Fail(ex, ct);
            throw;
        }

        try
        {
            while (true)
            {
                bool more;
                try
                {
                    more = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    op.Fail(ex, ct);
                    throw;
                }

                if (!more)
                {
                    break;
                }

                op.AddItems(1);
                yield return enumerator.Current;
            }

            op.Complete();
        }
        finally
        {
            // the caller stopped before the end (a no-op once the listing finished either way); first, so that a dispose that throws leaves no row running
            op.Cancel("stopped early");
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<IVfsFile> OpenAsync(string path, VfsOpenMode mode, CancellationToken ct)
    {
        var op = Begin(mode.IsWrite() ? OperationKind.WriteFile : OperationKind.ReadFile, path);
        IVfsFile file;
        try
        {
            file = await Inner.OpenAsync(path, mode, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            op.Fail(ex, ct);
            throw;
        }

        return new TrackedFile(file, op);
    }

    public Task CreateDirectoryAsync(string path, CancellationToken ct)
        => TrackAsync(Begin(OperationKind.CreateDirectory, path), () => Inner.CreateDirectoryAsync(path, ct), ct);

    public Task DeleteAsync(string path, CancellationToken ct)
        => TrackAsync(Begin(OperationKind.Delete, path), () => Inner.DeleteAsync(path, ct), ct);

    public Task RenameAsync(string fromPath, string toPath, bool replace, CancellationToken ct)
        => TrackAsync(Begin(OperationKind.Rename, fromPath, "to " + toPath), () => Inner.RenameAsync(fromPath, toPath, replace, ct), ct);

    public Task SetTimesAsync(string path, DateTimeOffset? created, DateTimeOffset? modified, CancellationToken ct)
        => TrackAsync(Begin(OperationKind.SetTimes, path), () => Inner.SetTimesAsync(path, created, modified, ct), ct);

    public Task<VfsSpace?> GetSpaceAsync(string path, CancellationToken ct)
        => TrackAsync(Begin(OperationKind.GetSpace, path), () => Inner.GetSpaceAsync(path, ct), ct);

    public async ValueTask DisposeAsync()
    {
        var op = Begin(OperationKind.Stop);
        try
        {
            await Inner.DisposeAsync().ConfigureAwait(false);
            op.Complete();
        }
        catch (Exception ex)
        {
            op.Fail(ex);
            throw;
        }
    }

    private static async Task TrackAsync(Operation op, Func<Task> call, CancellationToken ct)
    {
        try
        {
            await call().ConfigureAwait(false);
            op.Complete();
        }
        catch (Exception ex)
        {
            op.Fail(ex, ct);
            throw;
        }
    }

    private static async Task<T> TrackAsync<T>(Operation op, Func<Task<T>> call, CancellationToken ct)
    {
        try
        {
            var result = await call().ConfigureAwait(false);
            op.Complete();
            return result;
        }
        catch (Exception ex)
        {
            op.Fail(ex, ct);
            throw;
        }
    }

    /// <summary>The open file: bytes are counted on its operation, a failed call is noted on it, and closing it ends it.</summary>
    private sealed class TrackedFile(IVfsFile inner, Operation op) : IVfsFile
    {
        public long Length => inner.Length;

        public async ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct)
        {
            try
            {
                var n = await inner.ReadAsync(offset, buffer, ct).ConfigureAwait(false);
                op.AddBytes(n);
                return n;
            }
            catch (Exception ex)
            {
                op.Note(ex, ct);
                throw;
            }
        }

        public async ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            try
            {
                await inner.WriteAsync(offset, data, ct).ConfigureAwait(false);
                op.AddBytes(data.Length);
            }
            catch (Exception ex)
            {
                op.Note(ex, ct);
                throw;
            }
        }

        public async ValueTask SetLengthAsync(long length, CancellationToken ct)
        {
            try
            {
                await inner.SetLengthAsync(length, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                op.Note(ex, ct);
                throw;
            }
        }

        public async ValueTask FlushAsync(CancellationToken ct)
        {
            try
            {
                await inner.FlushAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                op.Note(ex, ct);
                throw;
            }
        }

        /// <summary>The close is part of the operation: a write-on-close upload that fails ends it Failed.</summary>
        public async ValueTask DisposeAsync()
        {
            try
            {
                await inner.DisposeAsync().ConfigureAwait(false);
                op.Complete();
            }
            catch (Exception ex)
            {
                op.Fail(ex);
                throw;
            }
        }
    }
}
