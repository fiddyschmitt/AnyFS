using System.Globalization;

namespace AnyFS.Sdk;

/// <summary>
/// What a mount that serves files over HTTP needs to tell before it opens one: whether the request's validators say the
/// client's copy still stands (RFC 9110 13.1), in which case nothing of the file is sent and it need not be opened. To a
/// source an open is a request of its server. The answer itself is still the web server's to make, by the same rule:
/// this only says whether the file will be wanted.
/// </summary>
public static class HttpValidators
{
    /// <summary>
    /// Whether a GET or HEAD carrying these If-None-Match and If-Modified-Since headers is answered "not modified" for
    /// a file with this entity tag and this time of change. If-None-Match decides where it is given (by the tags
    /// alone, weak or not); the time is looked at only without it, to the second, as the header has it.
    /// </summary>
    public static bool NotModified(string? ifNoneMatch, string? ifModifiedSince, string? entityTag, DateTimeOffset? modified)
    {
        if (!string.IsNullOrWhiteSpace(ifNoneMatch))
        {
            if (string.IsNullOrEmpty(entityTag))
            {
                return false;
            }

            // one tag is the usual case, and a tag may hold a comma of its own (SharePoint's do)
            var own = Opaque(entityTag);
            var given = ifNoneMatch.Trim();
            return given == "*" || Opaque(given) == own || given.Split(',').Any(tag => Opaque(tag.Trim()) == own);
        }

        return modified is { } changed
            && DateTimeOffset.TryParseExact(ifModifiedSince?.Trim(), "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var since)
            && changed.UtcTicks - (changed.UtcTicks % TimeSpan.TicksPerSecond) <= since.UtcTicks;
    }

    private static string Opaque(string tag) => tag.StartsWith("W/", StringComparison.Ordinal) ? tag[2..] : tag;
}
