using System.Net;
using AnyFS.Sdk;

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

    /// <summary>
    /// The body of <paramref name="response"/> to a request for a file from <paramref name="offset"/> on, as
    /// <see cref="OpenAsync"/>: a 206's as it comes (refused when it starts elsewhere); a server that ignored the range
    /// sends the whole file, and the part before the offset is read past, <paramref name="rangeIgnored"/> told first.
    /// </summary>
    public static async Task<Stream> FromOffsetAsync(HttpResponseMessage response, long offset, string path, Action? rangeIgnored, CancellationToken ct)
    {
        var body = await OpenAsync(response, ct).ConfigureAwait(false);
        if (offset == 0)
        {
            return body;
        }

        try
        {
            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                if (response.Content.Headers.ContentRange?.From is { } from && from != offset)
                {
                    throw new VfsException(VfsError.IoError, $"'{path}': asked for byte {offset:N0} on, the server sent from byte {from:N0}");
                }

                return body;
            }

            rangeIgnored?.Invoke();
            await SkipAsync(body, offset, ct).ConfigureAwait(false);
            return body;
        }
        catch
        {
            await body.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task SkipAsync(Stream body, long count, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        while (count > 0)
        {
            var n = await body.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count)), ct).ConfigureAwait(false);
            if (n <= 0)
            {
                throw new VfsException(VfsError.IoError, "the file ended before the place asked for");
            }

            count -= n;
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
