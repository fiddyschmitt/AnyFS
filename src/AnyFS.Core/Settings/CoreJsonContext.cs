using System.Text.Json.Serialization;
using AnyFS.Core.Model;
using AnyFS.Core.Stats;
using AnyFS.Core.Status;

namespace AnyFS.Core.Settings;

/// <summary>Source-generated JSON for AnyFS's own files (trim-safe; plugin POCOs go through reflection).</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(SourcesFile))]
[JsonSerializable(typeof(CollectionsFile))]
[JsonSerializable(typeof(SecretsFile))]
[JsonSerializable(typeof(StatsFile))]
[JsonSerializable(typeof(StatusSnapshot))]
public sealed partial class CoreJsonContext : JsonSerializerContext
{
}
