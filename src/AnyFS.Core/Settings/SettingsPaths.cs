namespace AnyFS.Core.Settings;

/// <summary>
/// Where everything lives (spec 9.1): a portable layout beside the executable when that folder is
/// writable, else the user's config folders; <c>--settings-dir</c> overrides both, with state, cache,
/// plugins and the log as its siblings.
/// </summary>
public sealed record SettingsPaths(string Settings, string State, string Cache, string Plugins, string LogFile, bool Portable)
{
    public static SettingsPaths Resolve(string? settingsDirOverride, string? exeDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(settingsDirOverride))
        {
            var settings = Path.GetFullPath(settingsDirOverride);
            var baseDir = Path.GetDirectoryName(settings) ?? settings;
            return new SettingsPaths(
                settings,
                Path.Combine(baseDir, "state"),
                Path.Combine(baseDir, "cache"),
                Path.Combine(baseDir, "plugins"),
                Path.Combine(baseDir, "anyfs.log"),
                Portable: false);
        }

        exeDirectory ??= AppContext.BaseDirectory;
        if (IsWritable(exeDirectory))
        {
            return new SettingsPaths(
                Path.Combine(exeDirectory, "settings"),
                Path.Combine(exeDirectory, "state"),
                Path.Combine(exeDirectory, "cache"),
                Path.Combine(exeDirectory, "plugins"),
                Path.Combine(exeDirectory, "anyfs.log"),
                Portable: true);
        }

        var (config, cache) = UserFolders();
        return new SettingsPaths(
            Path.Combine(config, "settings"),
            Path.Combine(config, "state"),
            cache,
            Path.Combine(config, "plugins"),
            Path.Combine(config, "anyfs.log"),
            Portable: false);
    }

    /// <summary>The per-source cache folder (spool files).</summary>
    public string SourceCache(Guid sourceId) => Path.Combine(Cache, "sources", sourceId.ToString("N"));

    public string SourceState(Guid sourceId) => Path.Combine(State, "sources", sourceId.ToString("N"));

    public string MountState(Guid mountId) => Path.Combine(State, "mounts", mountId.ToString("N"));

    /// <summary>The cache folder every mount of one provider shares (the SMB provider's unpacked server).</summary>
    public string MountProviderCache(string providerTypeId) => Path.Combine(Cache, "mount-providers", FolderName(providerTypeId));

    public string AppSettingsFile => Path.Combine(Settings, "app.json");

    public string SourcesFile => Path.Combine(Settings, "sources.json");

    public string CollectionsFile => Path.Combine(Settings, "collections.json");

    public string SecretsFile => Path.Combine(State, "secrets.json");

    public string KeysDirectory => Path.Combine(State, "keys");

    public string StatsFile => Path.Combine(State, "stats.json");

    public string StatusFile => Path.Combine(State, "status.json");

    private static (string Config, string Cache) UserFolders()
    {
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return (Path.Combine(appData, "AnyFS"), Path.Combine(local, "AnyFS", "cache"));
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsMacOS())
        {
            return (Path.Combine(home, "Library", "Application Support", "AnyFS"), Path.Combine(home, "Library", "Caches", "AnyFS"));
        }

        var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var xdgCache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        return (
            Path.Combine(string.IsNullOrEmpty(xdgConfig) ? Path.Combine(home, ".config") : xdgConfig, "anyfs"),
            Path.Combine(string.IsNullOrEmpty(xdgCache) ? Path.Combine(home, ".cache") : xdgCache, "anyfs"));
    }

    /// <summary>A plugin's type id as one safe folder name (a third-party id could hold a separator or be dots only).</summary>
    private static string FolderName(string typeId)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string(typeId.Select(c => c is '/' or '\\' || Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray()).Trim().Trim('.');
        return name.Length == 0 ? "_" : name;
    }

    private static bool IsWritable(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return false;
            }

            var probe = Path.Combine(directory, ".anyfs-write-probe-" + Guid.NewGuid().ToString("N")[..8]);
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
