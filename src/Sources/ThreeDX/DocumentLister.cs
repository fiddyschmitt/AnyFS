using AnyFS.Sdk;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.ThreeDX;

/// <summary>
/// Lists every document the source's search finds (spec 7.6), as Mount3DX does: 3DSearch for how many there are, then each
/// page of a hundred ids and their documents (<see cref="ThreeDXClient.DocumentsAsync"/>), several pages at once. A page
/// that fails is tried again a few times after a pause, longer each time, before the listing fails as a whole; the
/// documents come back in the search's order.
/// </summary>
internal static class DocumentLister
{
    public const int PageAttempts = 5;

    /// <param name="progress">Pages done, pages in all, documents so far.</param>
    public static async Task<IReadOnlyList<DxDocument>> ListAsync(ThreeDXClient client, string query, string tenant, int threads, Action<int, int, int>? progress, ILogger logger, CancellationToken ct, Func<int, TimeSpan>? pause = null)
    {
        pause ??= attempt => TimeSpan.FromSeconds(2 * attempt);
        var (count, searchId) = await client.CountAsync(query, tenant, ct).ConfigureAwait(false);
        var pages = (count + ThreeDXClient.PageSize - 1) / ThreeDXClient.PageSize;
        logger.LogInformation("3dx: the search finds {Count} documents: {Pages} pages to list, {Threads} at a time", count, pages, threads);
        var results = new IReadOnlyList<DxDocument>[pages];
        var done = 0;
        var documents = 0;
        progress?.Invoke(0, pages, 0);
        await Parallel.ForEachAsync(Enumerable.Range(0, pages), new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, threads), CancellationToken = ct }, async (page, pageCt) =>
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    var ids = await client.PageAsync(page, ThreeDXClient.PageSize, searchId, query, tenant, pageCt).ConfigureAwait(false);
                    results[page] = await client.DocumentsAsync(ids, pageCt).ConfigureAwait(false);
                    if (attempt > 1)
                    {
                        logger.LogInformation("3dx: page {Page} listed at attempt {Attempt}", page, attempt);
                    }

                    break;
                }
                catch (Exception ex) when (attempt < PageAttempts && Retriable(ex) && !pageCt.IsCancellationRequested)
                {
                    logger.LogWarning("3dx: page {Page} of the document list failed (attempt {Attempt} of {Attempts}): {Error}", page, attempt, PageAttempts, ex.Message);
                    await Task.Delay(pause(attempt), pageCt).ConfigureAwait(false);
                }
            }

            progress?.Invoke(Interlocked.Increment(ref done), pages, Interlocked.Add(ref documents, results[page].Count));
        }).ConfigureAwait(false);

        return results.SelectMany(r => r).ToList();
    }

    /// <summary>Whether trying again can help: not when 3DX refuses the account, nor when the sign-in needs a person.</summary>
    private static bool Retriable(Exception ex) => ex switch
    {
        SignInRequiredException => false,
        VfsException { Error: VfsError.AccessDenied or VfsError.InvalidPath } => false,
        _ => true,
    };
}
