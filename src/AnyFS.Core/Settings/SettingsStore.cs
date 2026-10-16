using System.Text.Json;
using AnyFS.Core.Model;
using AnyFS.Core.Plugins;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging;

namespace AnyFS.Core.Settings;

/// <summary>
/// Loads and saves app.json, sources.json and collections.json (spec 9.2): indented, stable property
/// order, atomic writes, a version field with a migration step, and defaults for a first start.
/// </summary>
public sealed class SettingsStore
{
    private readonly ILogger _logger;

    public SettingsStore(SettingsPaths paths, ILogger logger)
    {
        Paths = paths;
        _logger = logger;
    }

    public SettingsPaths Paths { get; }

    public AppSettings App { get; private set; } = new();

    public List<SourceDefinition> Sources { get; private set; } = [];

    public List<CollectionDefinition> Collections { get; private set; } = [];

    /// <summary>True when collections.json did not exist (so Collection 1 gets created).</summary>
    public bool FirstStart { get; private set; }

    public void Load()
    {
        Directory.CreateDirectory(Paths.Settings);
        App = ReadOrQuarantine(Paths.AppSettingsFile, CoreJsonContext.Default.AppSettings) ?? new AppSettings();
        CheckVersion(Paths.AppSettingsFile, App.Version);

        var sources = ReadOrQuarantine(Paths.SourcesFile, CoreJsonContext.Default.SourcesFile);
        if (sources is not null)
        {
            CheckVersion(Paths.SourcesFile, sources.Version);
        }

        Sources = sources?.Sources ?? [];

        var collections = ReadOrQuarantine(Paths.CollectionsFile, CoreJsonContext.Default.CollectionsFile);
        if (collections is not null)
        {
            CheckVersion(Paths.CollectionsFile, collections.Version);
        }

        FirstStart = collections is null;
        Collections = collections?.Collections ?? [];
        Tidy();
    }

    /// <summary>First start: "Collection 1", enabled, no sources, one mount of the native provider for this OS (spec 11.1, your Q14/N6).</summary>
    public bool EnsureDefaultCollection(PluginRegistry registry)
    {
        if (!FirstStart || Collections.Count > 0)
        {
            return false;
        }

        var providerId = NativeProviderForThisOs();
        var collection = new CollectionDefinition { Name = "Collection 1" };
        var provider = registry.FindMountProvider(providerId);
        var mount = new MountDefinition
        {
            Name = provider?.DisplayName ?? DefaultMountName(providerId),
            Provider = providerId,
        };
        if (provider is not null)
        {
            var info = new CollectionInfo(collection.Id, collection.Name);
            mount.Settings = PluginSettingsBinder.Serialise(provider.SettingsType, provider.CreateDefaultSettings(info));
        }

        collection.Mounts.Add(mount);
        Collections.Add(collection);
        SaveCollections();
        if (!File.Exists(Paths.AppSettingsFile))
        {
            SaveApp();
        }

        if (!File.Exists(Paths.SourcesFile))
        {
            SaveSources();
        }

        return true;
    }

    public static string NativeProviderForThisOs()
        => OperatingSystem.IsWindows() ? "winfsp" : OperatingSystem.IsLinux() ? "fuse" : "smb";

    public static string DefaultMountName(string providerId) => providerId switch
    {
        "winfsp" => "WinFsp",
        "fuse" => "FUSE",
        "smb" => "SMB",
        "nfs" => "NFS",
        "webdav" => "WebDAV",
        "ftp" => "FTP",
        "http" => "HTTP",
        _ => providerId,
    };

    /// <summary>The default source path of a source in a collection: <c>&lt;type folder&gt;/&lt;name&gt;</c>.</summary>
    public static string DefaultSourcePath(ISourcePlugin plugin, string sourceName) => plugin.TypeFolder + "/" + sourceName;

    public void SaveApp() => JsonFiles.WriteAtomic(Paths.AppSettingsFile, App, CoreJsonContext.Default.AppSettings);

    public void SaveSources()
        => JsonFiles.WriteAtomic(Paths.SourcesFile, new SourcesFile { Sources = Sources.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList() }, CoreJsonContext.Default.SourcesFile);

    public void SaveCollections()
        => JsonFiles.WriteAtomic(Paths.CollectionsFile, new CollectionsFile { Collections = Collections }, CoreJsonContext.Default.CollectionsFile);

    public CollectionDefinition? FindCollection(string name)
        => Collections.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public SourceDefinition? FindSource(Guid id) => Sources.FirstOrDefault(s => s.Id == id);

    /// <summary>Collections that reference a source, with the path it has in each.</summary>
    public IEnumerable<(CollectionDefinition Collection, SourceMembership Membership)> UsesOf(Guid sourceId)
        => Collections.SelectMany(c => c.Sources.Where(m => m.Id == sourceId).Select(m => (c, m)));

    private void Tidy()
    {
        var seen = new HashSet<Guid>();
        foreach (var source in Sources.ToList())
        {
            if (source.Id == Guid.Empty)
            {
                source.Id = Guid.NewGuid();
            }

            if (!seen.Add(source.Id))
            {
                _logger.LogWarning("sources.json: duplicate source id {Id} ({Name}); the second one was ignored", source.Id, source.Name);
                Sources.Remove(source);
            }
        }

        foreach (var collection in Collections)
        {
            if (collection.Id == Guid.Empty)
            {
                collection.Id = Guid.NewGuid();
            }

            foreach (var membership in collection.Sources.ToList())
            {
                if (!seen.Contains(membership.Id))
                {
                    // kept in the list (so a save doesn't lose it) but never started; the engine skips it
                    _logger.LogWarning("collections.json: collection '{Collection}' references a source {Id} that isn't in sources.json; it is ignored", collection.Name, membership.Id);
                }

                if (!VfsPath.TryNormalizeSourcePath(membership.Path, out var normalized, out var reason))
                {
                    _logger.LogWarning("collections.json: collection '{Collection}', source {Id}: path '{Path}' is invalid ({Reason}); using '{Fallback}'", collection.Name, membership.Id, membership.Path, reason, LenientPath(membership.Path));
                    normalized = LenientPath(membership.Path);
                }

                membership.Path = normalized;
            }

            foreach (var mount in collection.Mounts)
            {
                if (mount.Id == Guid.Empty)
                {
                    mount.Id = Guid.NewGuid();
                }

                if (string.IsNullOrWhiteSpace(mount.Name))
                {
                    mount.Name = DefaultMountName(mount.Provider);
                }
            }
        }
    }

    private static string LenientPath(string configured)
        => VfsPath.TryNormalize(configured.Trim(), out var n, out _) ? n : "";

    private T? ReadOrQuarantine<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
        where T : class
    {
        try
        {
            return JsonFiles.Read(path, typeInfo);
        }
        catch (JsonException ex)
        {
            var moved = JsonFiles.Quarantine(path);
            _logger.LogError(ex, "{File} could not be read and was moved to {Moved}; starting with defaults", Path.GetFileName(path), moved);
            return null;
        }
    }

    private void CheckVersion(string path, int version)
    {
        if (version > SettingsFormat.CurrentVersion)
        {
            _logger.LogWarning("{File} is version {Version}, newer than this AnyFS understands ({Current}); some settings may be ignored", Path.GetFileName(path), version, SettingsFormat.CurrentVersion);
        }
    }

    private sealed record CollectionInfo(Guid Id, string Name) : ICollectionInfo;
}
