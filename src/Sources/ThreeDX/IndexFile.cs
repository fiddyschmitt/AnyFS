using System.Text.Json.Serialization;

namespace AnyFS.Sources.ThreeDX;

/// <summary>
/// The documents a 3DX source last had from 3DX, kept in its state folder (spec 7.6), so a restart serves them at once and
/// asks 3DX afresh in the background instead of keeping the source starting for the minutes a big server takes to list.
/// Kept with what it was taken from (the server, the search, the tenant, the order dates were read in), so a source pointed
/// elsewhere, or reading dates otherwise, starts afresh; written whole and moved into place (<see cref="StateFile"/>), so a
/// crash leaves the last good one.
/// </summary>
internal static class IndexFile
{
    /// <summary>Bumped when the shape changes; an older file is then not read.</summary>
    public const int Version = 1;

    public const string FileName = "documents.json.gz";

    /// <summary>Writes the index whole and moves it into place (<see cref="StateFile.Save{T}"/>).</summary>
    public static void Save(string path, SavedIndex index) => StateFile.Save(path, index, IndexJson.Default.SavedIndex);

    /// <summary>The index saved at <paramref name="path"/>, or null when there is none, it cannot be read, or it was taken from somewhere else.</summary>
    public static SavedIndex? Load(string path, string server, string query, string tenant, string dates)
        => StateFile.Load(path, IndexJson.Default.SavedIndex) is { Version: Version } index && index.Server == server && index.Query == query && index.Tenant == tenant && index.Dates == dates
            ? index
            : null;
}

/// <summary>What <see cref="IndexFile"/> keeps: the documents, when they were taken and from where.</summary>
internal sealed record SavedIndex(int Version, string Server, string Query, string Tenant, string Dates, DateTimeOffset Taken, IReadOnlyList<DxDocument> Documents);

/// <summary>The index's JSON, generated at build time (no reflection, so trimming keeps it working).</summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SavedIndex))]
internal sealed partial class IndexJson : JsonSerializerContext;
