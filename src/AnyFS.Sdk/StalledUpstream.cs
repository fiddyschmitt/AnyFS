namespace AnyFS.Sdk;

/// <summary>
/// Reads from an upstream that may stop sending without closing: a connection that went quiet halfway through a body
/// (an HTTP download read after its headers has no timeout of its own). A read that gets nothing for the limit fails,
/// so whoever waits on it (a reader of a spool, a mount's request on one of its few threads) gets an error rather than
/// waiting for ever.
/// </summary>
internal static class StalledUpstream
{
    /// <summary>How long a read from an upstream may get nothing before it fails.</summary>
    public static readonly TimeSpan DefaultLimit = TimeSpan.FromSeconds(60);

    public static async ValueTask<int> ReadAsync(Stream stream, Memory<byte> buffer, TimeSpan limit, CancellationToken ct)
    {
        using var quiet = CancellationTokenSource.CreateLinkedTokenSource(ct);
        quiet.CancelAfter(limit);
        try
        {
            return await stream.ReadAsync(buffer, quiet.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw VfsException.Unavailable($"the upstream sent nothing for {limit.TotalSeconds:0.#} s");
        }
    }
}
