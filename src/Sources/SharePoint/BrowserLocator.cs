using System.Runtime.Versioning;

namespace AnyFS.Sources.SharePoint;

/// <summary>
/// Finds a Chromium-based browser (Edge, Chrome or Chromium) for <see cref="BrowserSession"/> to drive over the
/// DevTools protocol (spec 7.3.2). The person's own browser and its profile are never touched: the browser found
/// here is launched with a profile of AnyFS's own. The order prefers Edge on Windows (always present), then
/// Chrome, then Chromium; a settings path overrides the search, and <c>ANYFS_SHAREPOINT_BROWSER</c> overrides that
/// (for tests). Returns the executable's full path, or null when none is installed.
/// </summary>
internal static class BrowserLocator
{
    public static string? Find(string? configuredPath = null)
    {
        var overridePath = Environment.GetEnvironmentVariable("ANYFS_SHAREPOINT_BROWSER");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return File.Exists(overridePath) ? overridePath : null;
        }

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return File.Exists(configuredPath) ? configuredPath : null;
        }

        if (OperatingSystem.IsWindows())
        {
            return FindOnWindows();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new[]
            {
                "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
                "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
                "/Applications/Chromium.app/Contents/MacOS/Chromium",
            }.FirstOrDefault(File.Exists);
        }

        return FindOnPath("microsoft-edge", "microsoft-edge-stable", "google-chrome", "google-chrome-stable", "chromium", "chromium-browser");
    }

    /// <summary>A short name for the browser found, for messages ("Microsoft Edge", "Google Chrome").</summary>
    public static string DisplayName(string path)
    {
        var file = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        return file switch
        {
            _ when file.Contains("edge") => "Microsoft Edge",
            _ when file.Contains("chrome") => "Google Chrome",
            _ when file.Contains("chromium") => "Chromium",
            _ => "the browser",
        };
    }

    [SupportedOSPlatform("windows")]
    private static string? FindOnWindows()
    {
        // Edge ships with Windows and is always under Program Files (x86); Chrome goes to Program Files or, for a
        // per-user install, LocalAppData. Edge first, then Chrome, each machine-wide before per-user.
        var roots = new[]
        {
            Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
            Environment.GetEnvironmentVariable("ProgramFiles"),
            Environment.GetEnvironmentVariable("LocalAppData"),
        };
        var relative = new[]
        {
            @"Microsoft\Edge\Application\msedge.exe",
            @"Google\Chrome\Application\chrome.exe",
        };
        return (from rel in relative
                from root in roots
                where !string.IsNullOrEmpty(root)
                let full = Path.Combine(root!, rel)
                where File.Exists(full)
                select full).FirstOrDefault();
    }

    /// <summary>The first of <paramref name="names"/> found in a PATH directory, as a shell would find it (snap's /snap/bin included when it is on PATH).</summary>
    private static string? FindOnPath(params string[] names)
    {
        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        return (from name in names
                from dir in dirs
                let full = Path.Combine(dir, name)
                where File.Exists(full)
                select full).FirstOrDefault();
    }
}
