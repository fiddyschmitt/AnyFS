using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;

namespace AnyFS.Sources.Common;

/// <summary>The browsers the web session can sign in through: Chromium ones over DevTools or WebDriver, Firefox over WebDriver.</summary>
public enum BrowserKind
{
    Edge,
    Chrome,
    Chromium,
    Firefox,
}

/// <summary>
/// Finds the browsers the web session signs in through (spec 7.3.2). <see cref="Find"/> picks the Chromium-based one
/// (Edge, Chrome or Chromium) that <see cref="BrowserSession"/> drives over the DevTools protocol: Edge first on Windows
/// (always present), then Chrome, then Chromium; a settings path overrides the search, and the source's own variable
/// (<c>ANYFS_SHAREPOINT_BROWSER</c>, <c>ANYFS_3DX_BROWSER</c>) overrides that (for tests). <see cref="FindKind"/> and
/// <see cref="DefaultBrowser"/> serve the WebDriver fallback, which can use Firefox too. The person's own browser profile is
/// never touched: every launch uses a profile of AnyFS's own.
/// </summary>
public static class BrowserLocator
{
    /// <param name="configuredPath">The browser the settings name, if any.</param>
    /// <param name="overrideVariable">The environment variable whose path wins over both (tests).</param>
    public static string? Find(string? configuredPath = null, string? overrideVariable = null)
    {
        var overridePath = overrideVariable is null ? null : Environment.GetEnvironmentVariable(overrideVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return File.Exists(overridePath) ? overridePath : null;
        }

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return File.Exists(configuredPath) ? configuredPath : null;
        }

        return FindKind(BrowserKind.Edge) ?? FindKind(BrowserKind.Chrome) ?? FindKind(BrowserKind.Chromium);
    }

    /// <summary>The installed browser of one kind, or null.</summary>
    public static string? FindKind(BrowserKind kind)
    {
        if (OperatingSystem.IsWindows())
        {
            return FindOnWindows(kind);
        }

        if (OperatingSystem.IsMacOS())
        {
            var app = kind switch
            {
                BrowserKind.Edge => "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
                BrowserKind.Chrome => "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
                BrowserKind.Chromium => "/Applications/Chromium.app/Contents/MacOS/Chromium",
                _ => "/Applications/Firefox.app/Contents/MacOS/firefox",
            };
            return File.Exists(app) ? app : null;
        }

        return kind switch
        {
            BrowserKind.Edge => FindOnPath("microsoft-edge", "microsoft-edge-stable"),
            BrowserKind.Chrome => FindOnPath("google-chrome", "google-chrome-stable"),
            BrowserKind.Chromium => FindOnPath("chromium", "chromium-browser"),
            _ => FindOnPath("firefox"),
        };
    }

    /// <summary>The kind of the person's default web browser, as Windows records it (Mount3DX asks the same), or null elsewhere and when it is none of these.</summary>
    public static BrowserKind? DefaultBrowser()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using var choice = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\http\UserChoice");
        return KindOfProgId(choice?.GetValue("ProgId") as string);
    }

    /// <summary>ChromeHTML, FirefoxURL-308046B0AF4A39CB, MSEdgeHTM: the browser behind a URL handler's ProgId.</summary>
    internal static BrowserKind? KindOfProgId(string? progId) => (progId ?? "").ToLowerInvariant() switch
    {
        var p when p.Contains("chrome") => BrowserKind.Chrome,
        var p when p.Contains("firefox") => BrowserKind.Firefox,
        var p when p.Contains("edge") => BrowserKind.Edge,
        _ => null,
    };

    /// <summary>The kind of a browser executable, from its name.</summary>
    public static BrowserKind? KindOf(string path)
    {
        var file = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        return file switch
        {
            _ when file.Contains("edge") => BrowserKind.Edge,
            _ when file.Contains("chrome") => BrowserKind.Chrome,
            _ when file.Contains("chromium") => BrowserKind.Chromium,
            _ when file.Contains("firefox") => BrowserKind.Firefox,
            _ => null,
        };
    }

    /// <summary>A short name for the browser found, for messages ("Microsoft Edge", "Google Chrome").</summary>
    public static string DisplayName(string path) => KindOf(path) is { } kind ? DisplayName(kind) : "the browser";

    public static string DisplayName(BrowserKind kind) => kind switch
    {
        BrowserKind.Edge => "Microsoft Edge",
        BrowserKind.Chrome => "Google Chrome",
        BrowserKind.Chromium => "Chromium",
        _ => "Firefox",
    };

    /// <summary>
    /// Why the browser's policy keeps AnyFS from driving it over DevTools with a profile of its own, or null: remote
    /// debugging turned off (RemoteDebuggingAllowed = false), which WebDriver for the same browser needs too, or a user
    /// data directory fixed by policy (UserDataDir), which overrides AnyFS's <c>--user-data-dir</c> and hands the launch to
    /// the person's own browser. Read from the machine-wide and per-user policy keys on Windows and the managed policy
    /// files on Linux; a policy that is not on disk (pushed from the cloud, or macOS's managed preferences) shows only when
    /// the browser starts without a DevTools port. Firefox has no such policy.
    /// </summary>
    public static string? DevToolsBlocked(string browserPath) => KindOf(browserPath) is { } kind ? DevToolsBlocked(kind) : null;

    public static string? DevToolsBlocked(BrowserKind kind)
    {
        var (vendor, linuxPolicies) = kind switch
        {
            BrowserKind.Edge => (@"Microsoft\Edge", "/etc/opt/edge/policies/managed"),
            BrowserKind.Chrome => (@"Google\Chrome", "/etc/opt/chrome/policies/managed"),
            BrowserKind.Chromium => ("Chromium", "/etc/chromium/policies/managed"),
            _ => ("", ""),
        };
        if (vendor.Length == 0)
        {
            return null;
        }

        if (OperatingSystem.IsWindows())
        {
            return RegistryPolicyBlocks(vendor);
        }

        return OperatingSystem.IsLinux() ? PolicyFilesBlock(linuxPolicies) : null;
    }

    [SupportedOSPlatform("windows")]
    private static string? RegistryPolicyBlocks(string vendor)
    {
        foreach (var (hive, name) in new[] { (Registry.LocalMachine, "HKLM"), (Registry.CurrentUser, "HKCU") })
        {
            using var policies = hive.OpenSubKey($@"SOFTWARE\Policies\{vendor}");
            if (policies?.GetValue("RemoteDebuggingAllowed") is int allowed && allowed == 0)
            {
                return $@"remote debugging is turned off: {name}\SOFTWARE\Policies\{vendor}\RemoteDebuggingAllowed is 0";
            }

            if (policies?.GetValue("UserDataDir") is string dir && dir.Length > 0)
            {
                return $@"the browser's profile folder is fixed by policy ({name}\SOFTWARE\Policies\{vendor}\UserDataDir), so it will not take a profile of AnyFS's own";
            }
        }

        return null;
    }

    /// <summary>Why the first managed policy file in <paramref name="dir"/> that turns remote debugging off or fixes the profile folder blocks AnyFS, or null.</summary>
    internal static string? PolicyFilesBlock(string dir)
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
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (doc.RootElement.TryGetProperty("RemoteDebuggingAllowed", out var allowed) && allowed.ValueKind == JsonValueKind.False)
                {
                    return $"remote debugging is turned off: RemoteDebuggingAllowed is false in {file}";
                }

                if (doc.RootElement.TryGetProperty("UserDataDir", out var dataDir) && dataDir.ValueKind == JsonValueKind.String && dataDir.GetString()!.Length > 0)
                {
                    return $"the browser's profile folder is fixed by policy (UserDataDir in {file}), so it will not take a profile of AnyFS's own";
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
            }
        }

        return null;
    }

    [SupportedOSPlatform("windows")]
    private static string? FindOnWindows(BrowserKind kind)
    {
        // Edge ships with Windows and is always under Program Files (x86); Chrome and Firefox go to Program Files or, for
        // a per-user install, LocalAppData. Machine-wide before per-user.
        var roots = new[]
        {
            Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
            Environment.GetEnvironmentVariable("ProgramFiles"),
            Environment.GetEnvironmentVariable("LocalAppData"),
        };
        var relative = kind switch
        {
            BrowserKind.Edge => @"Microsoft\Edge\Application\msedge.exe",
            BrowserKind.Chrome => @"Google\Chrome\Application\chrome.exe",
            BrowserKind.Chromium => @"Chromium\Application\chrome.exe",
            _ => @"Mozilla Firefox\firefox.exe",
        };
        return (from root in roots
                where !string.IsNullOrEmpty(root)
                let full = Path.Combine(root!, relative)
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
