namespace AnyFS.Sources.Common;

/// <summary>A response body stream that disposes its response with it: what a source hands a spool or a ranged reader.</summary>
public sealed class ResponseStream(HttpResponseMessage response, Stream body) : Stream
{
    /// <summary>The body of <paramref name="response"/> (its headers read), as a stream that disposes the response with it.</summary>
    public static async Task<Stream> OpenAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return new ResponseStream(response, await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false));
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public override bool CanRead => body.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => body.Read(buffer, offset, count);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => body.ReadAsync(buffer, cancellationToken);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => body.ReadAsync(buffer, offset, count, cancellationToken);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            body.Dispose();
            response.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await body.DisposeAsync().ConfigureAwait(false);
        response.Dispose();
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
