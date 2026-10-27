namespace LibSmb;

/// <summary>Share-relative paths as the file system takes them, from the names SMB clients send.</summary>
internal static class SmbPath
{
    /// <summary>
    /// An SMB name (backslashes, as decoded from UTF-16) as a file system path: forward slashes, no leading or trailing
    /// slash, <c>.</c> and <c>..</c> resolved and anchored at the share's root, so no name can leave the share.
    /// </summary>
    public static string FromSmbName(string name)
    {
        var parts = new List<string>();
        foreach (var part in name.Replace('\\', '/').Split('/'))
        {
            switch (part)
            {
                case "" or ".":
                    break;
                case "..":
                    if (parts.Count > 0)
                    {
                        parts.RemoveAt(parts.Count - 1);
                    }

                    break;
                default:
                    parts.Add(part);
                    break;
            }
        }

        return string.Join('/', parts);
    }

    /// <summary>A directory's path and a name in it.</summary>
    public static string Join(string directory, string name) => directory.Length == 0 ? name : directory + "/" + name;

    /// <summary>The last part of a path; <c>""</c> for the root.</summary>
    public static string Name(string path) => path.Length == 0 ? "" : path[(path.LastIndexOf('/') + 1)..];
}

/// <summary>The search patterns of QUERY_DIRECTORY ([MS-SMB2] 2.2.33): <c>*</c> and <c>?</c>, and their DOS forms <c>&lt;</c> and <c>&gt;</c>.</summary>
internal static class SmbPattern
{
    /// <summary>A pattern that names one entry: not empty, with no wildcard.</summary>
    public static bool IsSingleName(string pattern) => pattern.Length > 0 && pattern.IndexOfAny(['*', '?', '<', '>', '"']) < 0;

    /// <summary>Whether <paramref name="name"/> matches <paramref name="pattern"/>; an empty pattern and <c>*</c> match everything.</summary>
    public static bool Matches(string pattern, string name, bool caseInsensitive)
    {
        if (pattern.Length == 0 || pattern == "*")
        {
            return true;
        }

        pattern = pattern.Replace('<', '*').Replace('>', '?');
        var comparison = caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (pattern.IndexOfAny(['*', '?']) < 0)
        {
            return string.Equals(pattern, name, comparison);
        }

        if (caseInsensitive)
        {
            pattern = pattern.ToLowerInvariant();
            name = name.ToLowerInvariant();
        }

        return Wildcard(pattern, name);
    }

    /// <summary>The usual linear backtracking match of <c>*</c> (any run) and <c>?</c> (any one character).</summary>
    private static bool Wildcard(string pattern, string name)
    {
        int p = 0, n = 0, star = -1, mark = 0;
        while (n < name.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || pattern[p] == name[n]))
            {
                p++;
                n++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = n;
            }
            else if (star >= 0)
            {
                p = star + 1;
                n = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }
}
