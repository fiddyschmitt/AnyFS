using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using AnyFS.Core.Settings;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnyFS.Core.Plugins;

/// <summary>One line of a plugin check: PASS, WARN or FAIL, and what was looked at.</summary>
public sealed record CheckLine(string Level, string Text)
{
    public override string ToString() => $"{Level,-5} {Text}";
}

public sealed class PluginCheckReport(IReadOnlyList<CheckLine> lines)
{
    public IReadOnlyList<CheckLine> Lines { get; } = lines;

    public bool Passed => Lines.All(l => l.Level != "FAIL");

    public int Failures => Lines.Count(l => l.Level == "FAIL");

    public int Warnings => Lines.Count(l => l.Level == "WARN");
}

/// <summary>
/// <c>anyfs plugin check &lt;dll&gt;</c> (spec 14.1): what a plugin author finds out before a user does. The DLL's
/// metadata is scanned for the assemblies it references: AnyFS.Sdk's version against the host's, every framework
/// assembly against the <see cref="GuaranteedFramework"/> set the trimmed host keeps (one outside it is simply not
/// there, and a plugin cannot ship a framework assembly), and every other assembly against the files beside the
/// plugin. Then the DLL is loaded the way the host loads it, its plugin types are listed, and each one's settings
/// are described, defaulted, round-tripped through JSON and validated.
/// </summary>
public static class PluginChecker
{
    public static PluginCheckReport Check(string dllPath)
    {
        var lines = new List<CheckLine>();
        if (!File.Exists(dllPath))
        {
            lines.Add(new("FAIL", $"{dllPath} does not exist"));
            return new PluginCheckReport(lines);
        }

        dllPath = Path.GetFullPath(dllPath);
        if (!ScanReferences(dllPath, lines))
        {
            return new PluginCheckReport(lines);
        }

        LoadAndExercise(dllPath, lines);
        return new PluginCheckReport(lines);
    }

    /// <summary>How one referenced assembly stands with the host; exposed for the tests and the messages.</summary>
    public static ReferenceVerdict Classify(string assemblyName, string pluginDirectory)
    {
        if (assemblyName == "AnyFS.Sdk")
        {
            return ReferenceVerdict.Sdk;
        }

        if (GuaranteedFramework.Contains(assemblyName))
        {
            return ReferenceVerdict.Kept;
        }

        if (assemblyName is "netstandard" or "mscorlib")
        {
            return ReferenceVerdict.Facade;
        }

        if (GuaranteedFramework.IsFrameworkAssembly(assemblyName))
        {
            return ReferenceVerdict.MissingFramework;
        }

        return File.Exists(Path.Combine(pluginDirectory, assemblyName + ".dll")) ? ReferenceVerdict.Beside : ReferenceVerdict.MissingBeside;
    }

    public enum ReferenceVerdict
    {
        Sdk,
        Kept,
        Facade,
        MissingFramework,
        Beside,
        MissingBeside,
    }

    private static bool ScanReferences(string dllPath, List<CheckLine> lines)
    {
        using var stream = File.OpenRead(dllPath);
        using var pe = new PEReader(stream);
        MetadataReader md;
        try
        {
            if (!pe.HasMetadata)
            {
                lines.Add(new("FAIL", $"{Path.GetFileName(dllPath)} is not a .NET assembly"));
                return false;
            }

            md = pe.GetMetadataReader();
        }
        catch (BadImageFormatException ex)
        {
            lines.Add(new("FAIL", $"{Path.GetFileName(dllPath)} is not a .NET assembly ({ex.Message})"));
            return false;
        }

        var definition = md.GetAssemblyDefinition();
        lines.Add(new("PASS", $"{md.GetString(definition.Name)} {definition.Version}, built for {TargetFramework(md) ?? "an unknown framework"}"));

        // which types each referenced assembly supplies, for the messages
        var typesByAssembly = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var handle in md.TypeReferences)
        {
            var typeRef = md.GetTypeReference(handle);
            var scope = typeRef.ResolutionScope;
            while (scope.Kind == HandleKind.TypeReference)
            {
                scope = md.GetTypeReference((TypeReferenceHandle)scope).ResolutionScope;
            }

            if (scope.Kind != HandleKind.AssemblyReference)
            {
                continue;
            }

            var assembly = md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)scope).Name);
            if (!typesByAssembly.TryGetValue(assembly, out var set))
            {
                typesByAssembly[assembly] = set = new SortedSet<string>(StringComparer.Ordinal);
            }

            set.Add(md.GetString(typeRef.Name));
        }

        var directory = Path.GetDirectoryName(dllPath)!;
        var sdkSeen = false;
        foreach (var handle in md.AssemblyReferences)
        {
            var reference = md.GetAssemblyReference(handle);
            var name = md.GetString(reference.Name);
            var types = typesByAssembly.TryGetValue(name, out var set) ? set : [];
            var sample = types.Count == 0 ? "" : $": {string.Join(", ", types.Take(6))}{(types.Count > 6 ? $" and {types.Count - 6} more" : "")}";
            switch (Classify(name, directory))
            {
                case ReferenceVerdict.Sdk:
                    sdkSeen = true;
                    lines.Add(PluginRegistry.IsSdkCompatible(reference.Version, out var reason)
                        ? new("PASS", $"AnyFS.Sdk {reference.Version.ToString(3)} (this anyfs has {SdkInfo.SdkVersion.ToString(3)})")
                        : new("FAIL", reason));
                    break;
                case ReferenceVerdict.Kept:
                    lines.Add(new("PASS", $"{name}: kept whole in the trimmed host{sample}"));
                    break;
                case ReferenceVerdict.Facade:
                    lines.Add(new("WARN", $"{name}: a .NET Standard / .NET Framework facade. Build the plugin for net10.0 so it references the host's own assemblies; the trimmed host keeps only the forwards something it carries uses{sample}"));
                    break;
                case ReferenceVerdict.MissingFramework:
                    lines.Add(new("FAIL", $"{name}: a framework assembly the trimmed host does not carry, and a plugin cannot ship one{sample}. Avoid it, or ask for it to join the guaranteed set (docs/plugin-authoring.md)"));
                    break;
                case ReferenceVerdict.Beside:
                    lines.Add(new("PASS", $"{name}: ships beside the plugin ({name}.dll){sample}"));
                    break;
                case ReferenceVerdict.MissingBeside:
                    lines.Add(new("FAIL", $"{name}: needed, but {name}.dll is not beside the plugin{sample}. Set <EnableDynamicLoading>true</EnableDynamicLoading> so a build copies the plugin's dependencies next to it, and ship them with it"));
                    break;
            }
        }

        if (!sdkSeen)
        {
            lines.Add(new("FAIL", "does not reference AnyFS.Sdk, so it cannot contain a plugin"));
            return false;
        }

        return true;
    }

    private static void LoadAndExercise(string dllPath, List<CheckLine> lines)
    {
        var registry = new PluginRegistry();
        int count;
        try
        {
            count = registry.LoadFile(dllPath, NullLogger.Instance);
        }
        catch (Exception ex)
        {
            lines.Add(new("FAIL", $"loading it threw {ex.GetType().Name}: {ex.GetBaseException().Message}"));
            return;
        }

        foreach (var info in registry.Plugins.Where(i => i.Origin == dllPath && i.Error is not null))
        {
            lines.Add(new("FAIL", info.Error!));
        }

        if (count == 0)
        {
            lines.Add(new("FAIL", "no plugin type was registered: a public, non-abstract class with a parameterless constructor must implement ISourcePlugin or IMountProviderPlugin"));
            return;
        }

        foreach (var plugin in registry.SourcePlugins.Values)
        {
            lines.Add(new("PASS", $"source plugin '{plugin.TypeId}' ({plugin.DisplayName}), type folder '{plugin.TypeFolder}'"));
            ExerciseSettings(plugin.SettingsType, plugin.CreateDefaultSettings, plugin as IValidatable, lines);
        }

        foreach (var provider in registry.MountProviders.Values)
        {
            var platforms = provider.SupportedPlatforms.Count == 0 ? "no platform" : string.Join(", ", provider.SupportedPlatforms);
            lines.Add(new("PASS", $"mount provider '{provider.TypeId}' ({provider.DisplayName}) for {platforms}, tier {provider.Tier}{(provider.UnavailableReason is { } why ? $"; unavailable here: {why}" : "")}"));
            ExerciseSettings(provider.SettingsType, () => provider.CreateDefaultSettings(new CheckCollection()), provider as IValidatable, lines);
        }
    }

    private static void ExerciseSettings(
        [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicProperties | System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.PublicConstructors)] Type settingsType,
        Func<object> defaults,
        IValidatable? validatable,
        List<CheckLine> lines)
    {
        try
        {
            var properties = PluginSettingsBinder.Describe(settingsType);
            var instance = defaults();
            if (instance.GetType() != settingsType)
            {
                lines.Add(new("FAIL", $"CreateDefaultSettings returned a {instance.GetType().Name}, not the declared {settingsType.Name}"));
                return;
            }

            var json = PluginSettingsBinder.Serialise(settingsType, instance);
            var back = PluginSettingsBinder.Materialise(settingsType, json);
            var again = PluginSettingsBinder.Serialise(settingsType, back);
            var stable = json.ToJsonString() == again.ToJsonString();
            var secrets = properties.Count(p => p.IsSecret);
            lines.Add(stable
                ? new("PASS", $"settings {settingsType.Name}: {properties.Count} field(s){(secrets > 0 ? $", {secrets} secret" : "")}; defaults round-trip through JSON")
                : new("FAIL", $"settings {settingsType.Name}: the defaults do not round-trip through JSON (a property that serialises differently from how it reads back)"));

            foreach (var p in properties.Where(p => p.IsSecret && p.Property.PropertyType != typeof(string)))
            {
                lines.Add(new("FAIL", $"settings {settingsType.Name}.{p.Name}: a Secret property must be a string"));
            }

            if (validatable is not null)
            {
                var messages = validatable.Validate(instance).ToList();
                var errors = messages.Count(m => m.IsError);
                lines.Add(new("PASS", $"Validate(defaults): {errors} error(s), {messages.Count - errors} warning(s){(errors > 0 ? " (fine: a fresh source usually needs its settings filled in)" : "")}"));
            }
        }
        catch (Exception ex)
        {
            lines.Add(new("FAIL", $"settings {settingsType.Name}: {ex.GetType().Name}: {ex.GetBaseException().Message}"));
        }
    }

    private static string? TargetFramework(MetadataReader md)
    {
        foreach (var handle in md.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attribute = md.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference)
            {
                continue;
            }

            var constructor = md.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (constructor.Parent.Kind != HandleKind.TypeReference || md.GetString(md.GetTypeReference((TypeReferenceHandle)constructor.Parent).Name) != "TargetFrameworkAttribute")
            {
                continue;
            }

            var blob = md.GetBlobReader(attribute.Value);
            if (blob.ReadUInt16() != 1)
            {
                return null;
            }

            return blob.ReadSerializedString();
        }

        return null;
    }

    private sealed class CheckCollection : ICollectionInfo
    {
        public Guid Id { get; } = Guid.NewGuid();

        public string Name => "check";
    }
}
