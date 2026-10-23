using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AnyFS.Sources.ThreeDX;

/// <summary>
/// The documents a 3DX source last had from 3DX, kept in its state folder (spec 7.6), so a restart serves them at once and
/// asks 3DX afresh in the background instead of keeping the source starting for the minutes a big server takes to list.
/// Kept with what it was taken from (the server, the search, the tenant, the order dates were read in), so a source pointed
/// elsewhere, or reading dates otherwise, starts afresh; written whole to a temporary file and moved into place, so a crash
/// leaves the last good one.
/// </summary>
internal static class IndexFile
{
    /// <summary>Bumped when the shape changes; an older file is then not read.</summary>
    public const int Version = 1;

    public const string FileName = "documents.json.gz";

    /// <summary>
    /// Writes the index whole beside <paramref name="path"/> and moves it into place; the move is tried again for a moment
    /// while something holds the old file open without letting it be replaced (an antivirus scanning it), which Windows
    /// refuses with access denied: a list that took minutes is not lost to that.
    /// </summary>
    public static void Save(string path, SavedIndex index)
    {
        var temp = path + ".tmp";
        using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var zip = new GZipStream(file, CompressionLevel.Fastest))
        {
            JsonSerializer.Serialize(zip, index, IndexJson.Default.SavedIndex);
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when ((ex is UnauthorizedAccessException or IOException) && attempt < 6)
            {
                Thread.Sleep(10 << (attempt - 1));
            }
        }
    }

    /// <summary>The index saved at <paramref name="path"/>, or null when there is none, it cannot be read, or it was taken from somewhere else.</summary>
    public static SavedIndex? Load(string path, string server, string query, string tenant, string dates)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var zip = new GZipStream(file, CompressionMode.Decompress);
            var index = JsonSerializer.Deserialize(zip, IndexJson.Default.SavedIndex);
            return index is { Version: Version } && index.Server == server && index.Query == query && index.Tenant == tenant && index.Dates == dates ? index : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>What <see cref="IndexFile"/> keeps: the documents, when they were taken and from where.</summary>
internal sealed record SavedIndex(int Version, string Server, string Query, string Tenant, string Dates, DateTimeOffset Taken, IReadOnlyList<DxDocument> Documents);

/// <summary>The index's JSON, generated at build time (no reflection, so trimming keeps it working).</summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SavedIndex))]
internal sealed partial class IndexJson : JsonSerializerContext;
