using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging;

namespace AnyFS.Core.Plugins;

/// <param name="Kind">"source" or "mount".</param>
/// <param name="Origin">"built-in" or the DLL's path.</param>
public sealed record PluginInfo(string TypeId, string Kind, string DisplayName, string Origin, string? Error = null);

/// <summary>
/// Every source plugin and mount provider the process knows: built-ins registered directly (no
/// reflection, so trimming is safe) and third-party DLLs from the plugins folder, each in its own load
/// context with <c>AnyFS.Sdk</c> resolved to the host's copy. A plugin that fails to load is recorded
/// and skipped, never fatal (spec 6.1).
/// </summary>
public sealed class PluginRegistry
{
    private const string Justification = "Third-party plugin DLLs are loaded at run time by design and are never trimmed; the SDK interfaces they implement are rooted in the host.";

    private readonly Dictionary<string, ISourcePlugin> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IMountProviderPlugin> _mounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PluginInfo> _infos = [];

    public IReadOnlyDictionary<string, ISourcePlugin> SourcePlugins => _sources;

    public IReadOnlyDictionary<string, IMountProviderPlugin> MountProviders => _mounts;

    public IReadOnlyList<PluginInfo> Plugins => _infos;

    public void AddBuiltIn(ISourcePlugin plugin) => Add(plugin, "built-in");

    public void AddBuiltIn(IMountProviderPlugin provider) => Add(provider, "built-in");

    public ISourcePlugin? FindSource(string typeId) => _sources.GetValueOrDefault(typeId);

    public IMountProviderPlugin? FindMountProvider(string typeId) => _mounts.GetValueOrDefault(typeId);

    /// <summary>The host refuses plugins built against a newer SDK major (and, before 1.0, a newer minor).</summary>
    public static bool IsSdkCompatible(Version? pluginSdk, out string reason)
    {
        var host = SdkInfo.SdkVersion;
        if (pluginSdk is null)
        {
            reason = "the plugin does not reference AnyFS.Sdk";
            return false;
        }

        var newer = host.Major == 0
            ? pluginSdk.Major > 0 || pluginSdk.Minor > host.Minor
            : pluginSdk.Major > host.Major;
        if (newer)
        {
            reason = $"built against AnyFS.Sdk {pluginSdk.ToString(3)}; this AnyFS has {host.ToString(3)}";
            return false;
        }

        reason = "";
        return true;
    }

    /// <summary>Loads every *.dll in <paramref name="directory"/>; returns how many plugin types were registered.</summary>
    public int LoadDirectory(string directory, ILogger logger)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var loaded = 0;
        foreach (var dll in Directory.GetFiles(directory, "*.dll").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            loaded += LoadFile(dll, logger);
        }

        return loaded;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = Justification)]
    [UnconditionalSuppressMessage("Trimming", "IL2062", Justification = Justification)]
    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = Justification)]
    [UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "Only plugin assemblies (never the single-file bundle) are inspected.")]
    public int LoadFile(string dll, ILogger logger)
    {
        var count = 0;
        try
        {
            var context = new PluginLoadContext(dll);
            var assembly = context.LoadFromAssemblyPath(dll);
            var sdkReference = assembly.GetReferencedAssemblies().FirstOrDefault(n => n.Name == "AnyFS.Sdk");
            if (sdkReference is null)
            {
                Record(dll, "does not reference AnyFS.Sdk", logger, LogLevel.Debug);
                return 0;
            }

            if (!IsSdkCompatible(sdkReference.Version, out var reason))
            {
                Record(dll, reason, logger, LogLevel.Warning);
                return 0;
            }

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t is not null).ToArray()!;
            }

            foreach (var type in types)
            {
                if (type.IsAbstract || !type.IsClass)
                {
                    continue;
                }

                var isSource = typeof(ISourcePlugin).IsAssignableFrom(type);
                var isMount = typeof(IMountProviderPlugin).IsAssignableFrom(type);
                if (!isSource && !isMount)
                {
                    continue;
                }

                try
                {
                    var instance = Activator.CreateInstance(type) ?? throw new InvalidOperationException("constructor returned null");
                    if (isSource)
                    {
                        Add((ISourcePlugin)instance, dll);
                    }
                    else
                    {
                        Add((IMountProviderPlugin)instance, dll);
                    }

                    count++;
                }
                catch (Exception ex)
                {
                    Record(dll, $"{type.FullName}: {ex.GetBaseException().Message}", logger, LogLevel.Warning);
                }
            }

            if (count == 0 && _infos.All(i => i.Origin != dll))
            {
                Record(dll, "contains no plugin types", logger, LogLevel.Debug);
            }
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or FileNotFoundException or TypeLoadException or InvalidOperationException)
        {
            Record(dll, ex.GetBaseException().Message, logger, LogLevel.Warning);
        }

        return count;
    }

    private void Add(ISourcePlugin plugin, string origin)
    {
        if (!_sources.TryAdd(plugin.TypeId, plugin))
        {
            _infos.Add(new PluginInfo(plugin.TypeId, "source", plugin.DisplayName, origin, $"source type '{plugin.TypeId}' is already registered by {_infos.First(i => i.TypeId.Equals(plugin.TypeId, StringComparison.OrdinalIgnoreCase) && i.Kind == "source").Origin}"));
            return;
        }

        _infos.Add(new PluginInfo(plugin.TypeId, "source", plugin.DisplayName, origin));
    }

    private void Add(IMountProviderPlugin provider, string origin)
    {
        if (!_mounts.TryAdd(provider.TypeId, provider))
        {
            _infos.Add(new PluginInfo(provider.TypeId, "mount", provider.DisplayName, origin, $"mount provider '{provider.TypeId}' is already registered by {_infos.First(i => i.TypeId.Equals(provider.TypeId, StringComparison.OrdinalIgnoreCase) && i.Kind == "mount").Origin}"));
            return;
        }

        _infos.Add(new PluginInfo(provider.TypeId, "mount", provider.DisplayName, origin));
    }

    private void Record(string dll, string error, ILogger logger, LogLevel level)
    {
        _infos.Add(new PluginInfo(Path.GetFileNameWithoutExtension(dll), "plugin", Path.GetFileName(dll), dll, error));
        logger.Log(level, "plugin {File}: {Error}", Path.GetFileName(dll), error);
    }

    /// <summary>One load context per plugin DLL; the SDK and anything the host already has come from the default context so interface types match.</summary>
    private sealed class PluginLoadContext(string pluginPath) : AssemblyLoadContext(Path.GetFileNameWithoutExtension(pluginPath), isCollectible: false)
    {
        private readonly AssemblyDependencyResolver _resolver = new(pluginPath);

        [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = Justification)]
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name is "AnyFS.Sdk" or "Microsoft.Extensions.Logging.Abstractions")
            {
                return null;
            }

            var path = _resolver.ResolveAssemblyToPath(name);
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}
