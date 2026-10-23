using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace AnyFS.Core.Settings;

/// <summary>Reads and atomically writes the JSON files under settings/ and state/.</summary>
public static class JsonFiles
{
    private const int ReplaceAttempts = 6;

    /// <summary>Null when the file doesn't exist. A corrupt file throws <see cref="JsonException"/>; callers back it up and start afresh.</summary>
    public static T? Read<T>(string path, JsonTypeInfo<T> typeInfo)
        where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        FileStream stream;
        try
        {
            stream = OpenToRead(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }

        using (stream)
        {
            if (stream.Length == 0)
            {
                return null;
            }

            return JsonSerializer.Deserialize(stream, typeInfo);
        }
    }

    /// <summary>
    /// Opens a file to read it, sharing it for writing and deleting too, so a writer can delete it as it is read. A rename
    /// over it (<see cref="WriteAtomic"/>) is still refused while it is open (Windows' MoveFileEx), which the writer waits out.
    /// </summary>
    public static FileStream OpenToRead(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>
    /// Writes to a temp file beside the target and renames over it, so a crash never leaves a half-written file. The rename
    /// is tried again for a moment while something else holds the target open without letting it be replaced (an
    /// antivirus scanning it, a reader that does not share it for deleting): Windows refuses it with access denied, or a
    /// sharing violation, until that lets go.
    /// </summary>
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

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(temp, path, overwrite: true);
                    break;
                }
                catch (Exception ex) when ((ex is UnauthorizedAccessException or IOException) && attempt < ReplaceAttempts)
                {
                    // 10, 20, 40, 80 and 160 ms: a third of a second in all
                    Thread.Sleep(10 << (attempt - 1));
                }
            }
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
