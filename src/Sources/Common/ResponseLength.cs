using System.Buffers;
using System.Net;
using System.Net.Http.Headers;

namespace AnyFS.Sources.Common;

/// <summary>
/// A file's length learnt from a server without keeping the file (the rule since 9-Nov-2026: a listed length comes from the
/// listing, or from a request measured and dropped). A server that keeps to ranges answers a request for the first byte or
/// few (<see cref="First"/>) with 206 and the whole file's length in its Content-Range, and nothing more crosses the network.
/// A GET for the whole file left unread is not as cheap as it looks: HttpClient reads what is left of a body of up to a
/// megabyte so that the connection can be used again, and drops the connection after a longer one's headers. A server that
/// ignores the range answers 200 with the whole file, its length in Content-Length where it gives one; where it gives none,
/// the body is counted as it comes (<see cref="CountAsync"/>). A HEAD does as well where a server answers it truly, but not
/// every one does: GitLab's says 0 for a job's log (the lab's GitLab 19.5, 27-Oct-2026), and what Teams' media service and
/// 3DX's file servers answer to one is not known, while both are known to keep to ranges.
/// </summary>
public static class ResponseLength
{
    /// <summary>A range of the first <paramref name="bytes"/> bytes of a file.</summary>
    public static RangeHeaderValue First(int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bytes, 1);
        return new RangeHeaderValue(0, bytes - 1);
    }

    /// <summary>Whether <paramref name="response"/> answers for part of the file alone (206, or 416 for a range past its end): its Content-Length is not the file's.</summary>
    public static bool IsPart(HttpResponseMessage response)
        => response.StatusCode is HttpStatusCode.PartialContent or HttpStatusCode.RequestedRangeNotSatisfiable;

    /// <summary>
    /// The whole file's length as <paramref name="response"/> gives it: a part's (206, 416) from its Content-Range, else its
    /// Content-Length; null where it gives none (a body sent in chunks, a part of a whole of unknown length).
    /// </summary>
    public static long? Of(HttpResponseMessage response)
        => IsPart(response) ? response.Content.Headers.ContentRange?.Length : response.Content.Headers.ContentLength;

    /// <summary>
    /// What is left of <paramref name="body"/> counted, nothing kept, on from the <paramref name="counted"/> bytes already
    /// read of it; null as soon as the count passes <paramref name="most"/> (where one is given), the rest left unread.
    /// </summary>
    public static async Task<long?> CountAsync(Stream body, long counted, long? most, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            for (int n; (n = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0;)
            {
                counted += n;
                if (counted > most)
                {
                    return null;
                }
            }

            return counted;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
