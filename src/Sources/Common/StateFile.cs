using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace AnyFS.Sources.Common;

/// <summary>
/// A file a source keeps in its state folder between starts (a listing it can serve at once): gzipped JSON, written whole
/// to a temporary file and moved into place, so a crash leaves the last good one. Its JSON goes through a generated
/// context (no reflection, so trimming keeps it working).
/// </summary>
public static class StateFile
{
    /// <summary>
    /// Writes <paramref name="value"/> whole beside <paramref name="path"/> and moves it into place; the move is tried
    /// again for a moment while something holds the old file open without letting it be replaced (an antivirus scanning
    /// it), which Windows refuses with access denied: a listing that took minutes is not lost to that.
    /// </summary>
    public static void Save<T>(string path, T value, JsonTypeInfo<T> json)
    {
        var temp = path + ".tmp";
        using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var zip = new GZipStream(file, CompressionLevel.Fastest))
        {
            JsonSerializer.Serialize(zip, value, json);
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

    /// <summary>What is kept at <paramref name="path"/>, or null when there is nothing, or it cannot be read.</summary>
    public static T? Load<T>(string path, JsonTypeInfo<T> json)
        where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var zip = new GZipStream(file, CompressionMode.Decompress);
            return JsonSerializer.Deserialize(zip, json);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }
}
