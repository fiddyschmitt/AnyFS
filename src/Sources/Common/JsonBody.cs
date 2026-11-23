using System.Text.Json;

namespace AnyFS.Sources.Common;

/// <summary>
/// An answer's JSON, read from its bytes. Five clients read the answer as text and parsed that (the review of
/// 21-Nov-2026, P11): every character twice the size it came in, and for a body over some 42 KB a string on the large
/// object heap, tens of thousands of times a walk.
/// </summary>
public static class JsonBody
{
    /// <summary>
    /// The JSON an answer carries; an empty object where it carries nothing (a 204, a success with no body). The body
    /// is held whole first where it was not already, so its length is known and it can be read again for an error's
    /// words. A body that is not JSON throws <see cref="JsonException"/>, for the caller to word.
    /// </summary>
    public static async Task<JsonDocument> ReadAsync(HttpContent content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        await content.LoadIntoBufferAsync(ct).ConfigureAwait(false);
        if (content.Headers.ContentLength == 0)
        {
            return JsonDocument.Parse("{}");
        }

        var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        }
    }
}
