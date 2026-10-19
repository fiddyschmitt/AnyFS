using System.Threading.Channels;

namespace AnyFS.Sdk;

/// <summary>
/// "Count files on start" (spec 6.8) with several folders listed at a time. A remote source spends most of a walk
/// waiting for each listing (a web request, an SFTP round trip), so a number of listings in flight make the walk about
/// that many times quicker: the host walks every source that is not <see cref="ICountable"/> this way through its
/// <see cref="IVfsOperations.ListAsync"/>. A source whose own listing is cheaper, or that should not descend into all
/// that a listing shows (SSHFS leaves symbolic links unfollowed), implements <see cref="ICountable"/> and hands its
/// listing to <see cref="CountAsync(Func{string, CancellationToken, Task{ValueTuple{long, IReadOnlyList{string}}}}, SourceCounters, int, Action{string, VfsException}?, CancellationToken)"/>.
/// </summary>
public static class TreeCount
{
    /// <summary>Listings in flight in the host's walk.</summary>
    public const int DefaultParallelism = 8;

    /// <summary>
    /// Counts the tree from the root into <paramref name="counters"/>: each folder's files and subfolders are added as its
    /// listing completes, with up to <paramref name="parallelism"/> listings at once. A folder whose listing throws
    /// <see cref="VfsException"/> is skipped, its subtree with it, and reported to <paramref name="skipped"/>; any other
    /// exception stops the walk and is thrown.
    /// </summary>
    /// <param name="listFolder">Lists one folder ("" for the root): how many files it holds, and the names of the folders in it to walk next (each counted as a folder).</param>
    public static async Task CountAsync(Func<string, CancellationToken, Task<(long Files, IReadOnlyList<string> Folders)>> listFolder, SourceCounters counters, int parallelism, Action<string, VfsException>? skipped, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(listFolder);
        ArgumentNullException.ThrowIfNull(counters);
        ArgumentOutOfRangeException.ThrowIfLessThan(parallelism, 1);

        var pending = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
        var outstanding = 1;
        pending.Writer.TryWrite("");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        async Task WorkerAsync()
        {
            await foreach (var dir in pending.Reader.ReadAllAsync(stop.Token).ConfigureAwait(false))
            {
                try
                {
                    var (files, folders) = await listFolder(dir, stop.Token).ConfigureAwait(false);
                    foreach (var folder in folders)
                    {
                        Interlocked.Increment(ref outstanding);
                        pending.Writer.TryWrite(VfsPath.Join(dir, folder));
                    }

                    counters.Add(files, folders.Count);
                }
                catch (VfsException ex)
                {
                    skipped?.Invoke(dir, ex);
                }
                catch
                {
                    // anything else ends the walk: the other workers stop at their next folder
                    await stop.CancelAsync().ConfigureAwait(false);
                    throw;
                }
                finally
                {
                    if (Interlocked.Decrement(ref outstanding) == 0)
                    {
                        pending.Writer.TryComplete();
                    }
                }
            }
        }

        var workers = Enumerable.Range(0, parallelism).Select(_ => Task.Run(WorkerAsync, CancellationToken.None)).ToList();
        await Task.WhenAll(workers).ConfigureAwait(false);
    }

    /// <summary>The same through a source's listing: folders are walked, everything else counts as a file.</summary>
    public static Task CountAsync(IVfsOperations source, SourceCounters counters, int parallelism, Action<string, VfsException>? skipped, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        return CountAsync(
            async (dir, token) =>
            {
                long files = 0;
                var folders = new List<string>();
                await foreach (var entry in source.ListAsync(dir, token).ConfigureAwait(false))
                {
                    if (entry.IsDirectory)
                    {
                        folders.Add(entry.Name);
                    }
                    else
                    {
                        files++;
                    }
                }

                return (files, folders);
            },
            counters,
            parallelism,
            skipped,
            ct);
    }
}
