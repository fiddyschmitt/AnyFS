using AnyFS.Sdk;

namespace AnyFS.Sources.Common;

/// <summary>
/// What a repository folder's entry keeps for its source beside its name, size and time (<see cref="VfsEntry.Key"/>), so that
/// a kept listing is enough to find and read what it names, however deep, with nothing asked of the server: a file's
/// content id is its entry's ETag already; the key says what else, and only where there is something to say, so most
/// entries have none. A file in Git LFS is marked (its bytes are the LFS object's, read by another request); a submodule is
/// marked (an empty folder); and where the name shown is not git's (made safe for Windows, or a twin in case numbered),
/// git's name follows a slash, which no git name holds. GitLab's since 16-Nov-2026, when its own copy of every repository
/// folder read in a run went (in memory, bounded at a million folders, and read again at every restart); Bitbucket's
/// too since 28-Nov-2026, which until then asked for every folder a file was opened through, once more in every run.
/// </summary>
public static class RepositoryKeys
{
    /// <summary>The form of the keys: listings kept in another are not read back (a source's SaveKey names it).</summary>
    public const int Format = 1;

    private const char InLfs = 'L';
    private const char IsSubmodule = 'S';
    private const char NameFollows = '/';

    /// <summary>The key of a repository entry; null where the entry says it all (git's name shown, a plain file or folder).</summary>
    public static string? Of(string gitName, string shown, bool lfs = false, bool submodule = false)
    {
        var flags = lfs ? InLfs.ToString() : submodule ? IsSubmodule.ToString() : "";
        var key = string.Equals(gitName, shown, StringComparison.Ordinal) ? flags : flags + NameFollows + gitName;
        return key.Length == 0 ? null : key;
    }

    /// <summary>What a repository entry's key says: git's name for it, whether it is a file in LFS, whether a submodule.</summary>
    public static (string GitName, bool Lfs, bool Submodule) Read(VfsEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Key is not { } key)
        {
            return (entry.Name, false, false);
        }

        var slash = key.IndexOf(NameFollows, StringComparison.Ordinal);
        var flags = slash < 0 ? key : key[..slash];
        return (slash < 0 ? entry.Name : key[(slash + 1)..], flags.Contains(InLfs, StringComparison.Ordinal), flags.Contains(IsSubmodule, StringComparison.Ordinal));
    }
}
