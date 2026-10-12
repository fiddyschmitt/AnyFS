using System.Buffers;
using System.Text;

namespace AnyFS.Sdk;

/// <summary>
/// Path rules for the virtual tree. A VFS path is '/'-separated, relative, with no leading or trailing
/// slash; the root is "". Backslashes are accepted on input and turned into slashes.
/// </summary>
public static class VfsPath
{
    public const int MaxSegmentBytes = 255;

    private static readonly SearchValues<char> SegmentInvalid = SearchValues.Create("\\/:*?\"<>|");

    /// <summary>Normalises a client path. Throws <see cref="VfsError.InvalidPath"/> for "." or ".." segments.</summary>
    public static string Normalize(string path)
    {
        return TryNormalize(path, out var normalized, out var reason) ? normalized : throw VfsException.InvalidPath(path, reason);
    }

    public static bool TryNormalize(string path, out string normalized, out string reason)
    {
        ArgumentNullException.ThrowIfNull(path);
        reason = "";
        if (path.Length == 0)
        {
            normalized = "";
            return true;
        }

        if (path.IndexOf('\\') < 0 && path.IndexOf("//", StringComparison.Ordinal) < 0 && path[0] != '/' && path[^1] != '/' && !HasDotSegment(path))
        {
            // the common case: already normalised, no allocation
            if (path.Contains('\0'))
            {
                normalized = "";
                reason = "contains a NUL character";
                return false;
            }

            normalized = path;
            return true;
        }

        var sb = new StringBuilder(path.Length);
        foreach (var raw in path.Split('/', '\\'))
        {
            if (raw.Length == 0)
            {
                continue;
            }

            if (raw is "." or "..")
            {
                normalized = "";
                reason = "'.' and '..' segments are not allowed";
                return false;
            }

            if (raw.Contains('\0'))
            {
                normalized = "";
                reason = "contains a NUL character";
                return false;
            }

            if (sb.Length > 0)
            {
                sb.Append('/');
            }

            sb.Append(raw);
        }

        normalized = sb.ToString();
        return true;
    }

    private static bool HasDotSegment(string path)
    {
        foreach (var seg in path.Split('/'))
        {
            if (seg is "." or "..")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Splits a normalised path into segments; the root gives an empty array.</summary>
    public static string[] Split(string normalizedPath)
        => normalizedPath.Length == 0 ? [] : normalizedPath.Split('/');

    public static string Join(string parent, string name)
        => parent.Length == 0 ? name : name.Length == 0 ? parent : parent + "/" + name;

    public static string Join(IEnumerable<string> segments) => string.Join('/', segments);

    public static string Parent(string normalizedPath)
    {
        var i = normalizedPath.LastIndexOf('/');
        return i < 0 ? "" : normalizedPath[..i];
    }

    public static string Name(string normalizedPath)
    {
        var i = normalizedPath.LastIndexOf('/');
        return i < 0 ? normalizedPath : normalizedPath[(i + 1)..];
    }

    public static (string Parent, string Name) SplitLast(string normalizedPath) => (Parent(normalizedPath), Name(normalizedPath));

    /// <summary>True when <paramref name="path"/> is <paramref name="ancestor"/> or lies under it (ordinal comparison).</summary>
    public static bool IsUnder(string path, string ancestor)
        => ancestor.Length == 0 || path.Equals(ancestor, StringComparison.Ordinal) || (path.Length > ancestor.Length && path[ancestor.Length] == '/' && path.StartsWith(ancestor, StringComparison.Ordinal));

    /// <summary>
    /// The rules for a configured source-path segment: valid on all three OSes, no leading or trailing
    /// dots or spaces, not "." or "..", at most 255 bytes of UTF-8.
    /// </summary>
    public static bool IsValidSegment(string segment, out string reason)
    {
        if (segment.Length == 0)
        {
            reason = "a segment is empty";
            return false;
        }

        if (segment is "." or "..")
        {
            reason = "'.' and '..' are not allowed";
            return false;
        }

        var bad = segment.AsSpan().IndexOfAny(SegmentInvalid);
        if (bad >= 0)
        {
            reason = $"'{segment[bad]}' is not allowed in a name";
            return false;
        }

        foreach (var c in segment)
        {
            if (c < ' ')
            {
                reason = "control characters are not allowed";
                return false;
            }
        }

        if (segment[0] == ' ' || segment[^1] == ' ' || segment[0] == '.' || segment[^1] == '.')
        {
            reason = "a name can't start or end with a space or a dot";
            return false;
        }

        if (Encoding.UTF8.GetByteCount(segment) > MaxSegmentBytes)
        {
            reason = $"a name can't be longer than {MaxSegmentBytes} bytes";
            return false;
        }

        reason = "";
        return true;
    }

    /// <summary>Normalises a source path as typed in the UI or settings and checks every segment.</summary>
    public static bool TryNormalizeSourcePath(string configured, out string normalized, out string reason)
    {
        if (!TryNormalize(configured.Trim(), out normalized, out reason))
        {
            return false;
        }

        foreach (var seg in Split(normalized))
        {
            if (!IsValidSegment(seg, out reason))
            {
                normalized = "";
                return false;
            }
        }

        return true;
    }
}
