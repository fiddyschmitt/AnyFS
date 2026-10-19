using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;

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

    /// <summary>
    /// Where the browser's policy turns remote debugging off (RemoteDebuggingAllowed = false), which the web session reads
    /// the sign-in through, or null: the machine-wide and per-user policy keys on Windows, the managed policy files on
    /// Linux. A policy that is not on disk (pushed from the cloud, or macOS's managed preferences) shows only when the
    /// browser starts without a DevTools port.
    /// </summary>
    public static string? RemoteDebuggingOff(string browserPath)
    {
        var (vendor, linuxPolicies) = DisplayName(browserPath) switch
        {
            "Microsoft Edge" => (@"Microsoft\Edge", "/etc/opt/edge/policies/managed"),
            "Google Chrome" => (@"Google\Chrome", "/etc/opt/chrome/policies/managed"),
            "Chromium" => ("Chromium", "/etc/chromium/policies/managed"),
            _ => ("", ""),
        };
        if (vendor.Length == 0)
        {
            return null;
        }

        if (OperatingSystem.IsWindows())
        {
            return RegistryPolicyOff(vendor);
        }

        return OperatingSystem.IsLinux() ? PolicyFilesOff(linuxPolicies) : null;
    }

    [SupportedOSPlatform("windows")]
    private static string? RegistryPolicyOff(string vendor)
    {
        foreach (var (hive, name) in new[] { (Registry.LocalMachine, "HKLM"), (Registry.CurrentUser, "HKCU") })
        {
            using var policies = hive.OpenSubKey($@"SOFTWARE\Policies\{vendor}");
            if (policies?.GetValue("RemoteDebuggingAllowed") is int allowed && allowed == 0)
            {
                return $@"{name}\SOFTWARE\Policies\{vendor}\RemoteDebuggingAllowed is 0";
            }
        }

        return null;
    }

    /// <summary>The first managed policy file in <paramref name="dir"/> that sets RemoteDebuggingAllowed to false, or null.</summary>
    internal static string? PolicyFilesOff(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return null;
        }

        foreach (var file in Directory.EnumerateFiles(dir, "*.json").Order(StringComparer.Ordinal))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("RemoteDebuggingAllowed", out var allowed) && allowed.ValueKind == JsonValueKind.False)
                {
                    return file;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
            }
        }

        return null;
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
