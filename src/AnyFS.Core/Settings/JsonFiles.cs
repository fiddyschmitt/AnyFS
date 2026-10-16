using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace AnyFS.Core.Settings;

/// <summary>Reads and atomically writes the JSON files under settings/ and state/.</summary>
public static class JsonFiles
{
    /// <summary>Null when the file doesn't exist. A corrupt file throws <see cref="JsonException"/>; callers back it up and start afresh.</summary>
    public static T? Read<T>(string path, JsonTypeInfo<T> typeInfo)
        where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using var stream = File.OpenRead(path);
        if (stream.Length == 0)
        {
            return null;
        }

        return JsonSerializer.Deserialize(stream, typeInfo);
    }

    /// <summary>Writes to a temp file beside the target and renames over it, so a crash never leaves a half-written file.</summary>
    public static void WriteAtomic<T>(string path, T value, JsonTypeInfo<T> typeInfo)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, typeInfo);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try
                {
                    File.Delete(temp);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    /// <summary>Moves a file that failed to parse out of the way as "name.bad-yyyyMMddHHmmss".</summary>
    public static string Quarantine(string path)
    {
        var target = $"{path}.bad-{DateTime.UtcNow:yyyyMMddHHmmss}";
        File.Move(path, target, overwrite: true);
        return target;
    }
}
