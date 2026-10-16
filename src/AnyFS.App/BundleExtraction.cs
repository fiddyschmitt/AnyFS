using Microsoft.Extensions.Logging;

namespace AnyFS.App;

/// <summary>
/// The single-file host unpacks anyfs's native libraries (Skia, HarfBuzz, ANGLE) into <c>%TEMP%\.net\anyfs\&lt;build&gt;\</c>,
/// or <c>~/.net/anyfs/&lt;build&gt;/</c> on Linux and macOS, and never removes the folder of a previous build, so every
/// build a user ran leaves some 18 MB behind. At start-up anyfs removes the other builds' folders: one copy per
/// version, like the SMB server in the cache. A folder a running anyfs still has loaded, or one a starting anyfs may
/// still be writing, is left alone; the host puts a deleted folder back the next time that build starts.
/// </summary>
internal static class BundleExtraction
{
    /// <summary>The host's folder for this executable's unpacked files is named after it.</summary>
    public const string AppName = "anyfs";

    /// <summary>A folder written more recently than this may belong to an anyfs that is still starting.</summary>
    public static readonly TimeSpan SettleTime = TimeSpan.FromMinutes(10);

    private static StringComparison PathComparison => OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>Removes the other builds' folders in the background; nothing happens outside a single-file bundle.</summary>
    public static void CleanUpInBackground(ILogger logger)
    {
        if (OwnDirectory() is not { } own)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                var removed = RemoveOtherBuilds(own, SettleTime);
                if (removed.Count > 0)
                {
                    logger.LogInformation("removed {Count} folder(s) of other builds' unpacked libraries beside {Own}", removed.Count, own);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "unpacked-library cleanup");
            }
        });
    }

    /// <summary>
    /// The folder this process was unpacked into: the entry in the runtime's native search path that sits under
    /// <c>.net/anyfs/</c>. Null in a normal (non-bundle) build, or when the host was pointed elsewhere.
    /// </summary>
    public static string? OwnDirectory()
    {
        if (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") is not string directories)
        {
            return null;
        }

        var appDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        foreach (var raw in directories.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string dir;
            try
            {
                dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw));
            }
            catch (Exception ex) when (ex is ArgumentException or IOException)
            {
                continue;
            }

            if (string.Equals(dir, appDir, PathComparison))
            {
                continue;
            }

            var parent = Directory.GetParent(dir);
            if (parent?.Parent is { } grandparent
                && string.Equals(parent.Name, AppName, PathComparison)
                && string.Equals(grandparent.Name, ".net", PathComparison)
                && Directory.Exists(dir))
            {
                return dir;
            }
        }

        return null;
    }

    /// <summary>
    /// Deletes the sibling folders of <paramref name="ownDirectory"/> that are older than <paramref name="settleTime"/>
    /// and whose files nothing has open. Returns the names of the folders removed.
    /// </summary>
    public static IReadOnlyList<string> RemoveOtherBuilds(string ownDirectory, TimeSpan settleTime)
    {
        var own = new DirectoryInfo(ownDirectory);
        if (own.Parent is not { Exists: true } parent)
        {
            return [];
        }

        var cutoff = DateTime.UtcNow - settleTime;
        var removed = new List<string>();
        foreach (var sibling in parent.EnumerateDirectories())
        {
            // the host fills a folder file by file and renames it into place, so one still being written is recent
            if (string.Equals(sibling.Name, own.Name, PathComparison) || sibling.LastWriteTimeUtc > cutoff || IsInUse(sibling))
            {
                continue;
            }

            try
            {
                sibling.Delete(recursive: true);
                removed.Add(sibling.Name);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // something took a file in the meantime; next start
            }
        }

        return removed;
    }

    /// <summary>
    /// On Windows a loaded library cannot be opened for exclusive access, so one such file means a running anyfs owns the
    /// folder and nothing in it may go (it might still load the rest). Elsewhere a mapped library survives the unlink,
    /// and the libraries a window needs are all loaded by the time it shows.
    /// </summary>
    private static bool IsInUse(DirectoryInfo directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        foreach (var file in directory.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            try
            {
                using var probe = new FileStream(file.FullName, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return true;
            }
        }

        return false;
    }
}
