using System.Text.Json;
using System.Text.Json.Nodes;
using AnyFS.Core.Model;
using AnyFS.Core.Settings;

namespace AnyFS.Core.Runtime;

/// <summary>What to do with an imported source whose id is already here (spec 9.5).</summary>
public enum ImportConflict
{
    /// <summary>The file's definition takes the id over; the source restarts.</summary>
    Replace,
    /// <summary>Mine stays; the file's is skipped.</summary>
    Keep,
    /// <summary>The file's comes in under a new id (and a distinct name).</summary>
    Copy,
}

public sealed record ImportSummary(int Added, int Replaced, int Kept, int Copied)
{
    public int Total => Added + Replaced + Kept + Copied;

    public string Describe() => $"{Added} added, {Replaced} replaced, {Kept} kept, {Copied} imported as copies";
}

/// <summary>
/// Sources files for sharing (spec 9.5): the same shape as settings/sources.json, so one can be dropped in as the
/// other, and never with secrets (the stored settings JSON has none; a hand-written one that carries some has them
/// moved to the secret store on import).
/// </summary>
public static class SourceTransfer
{
    public static void Write(string path, IEnumerable<SourceDefinition> sources)
    {
        var file = new SourcesFile
        {
            Sources = sources.Select(Copy).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList(),
        };
        JsonFiles.WriteAtomic(path, file, CoreJsonContext.Default.SourcesFile);
    }

    /// <summary>The sources in a file written by <see cref="Write"/> (or any sources.json); an <see cref="ArgumentException"/> says plainly what is wrong with it.</summary>
    public static IReadOnlyList<SourceDefinition> Read(string path)
    {
        SourcesFile? file;
        try
        {
            file = JsonFiles.Read(path, CoreJsonContext.Default.SourcesFile);
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"{Path.GetFileName(path)} is not a sources file: {ex.Message}", ex);
        }

        if (file is null)
        {
            throw new ArgumentException($"{Path.GetFileName(path)} is empty.");
        }

        if (file.Version != 1)
        {
            throw new ArgumentException($"{Path.GetFileName(path)} is a version {file.Version} sources file; this AnyFS reads version 1.");
        }

        var list = new List<SourceDefinition>();
        foreach (var source in file.Sources)
        {
            if (string.IsNullOrWhiteSpace(source.Name) || string.IsNullOrWhiteSpace(source.Type))
            {
                throw new ArgumentException($"{Path.GetFileName(path)}: every source needs a name and a type.");
            }

            if (source.Id == Guid.Empty)
            {
                source.Id = Guid.NewGuid();
            }

            source.Name = source.Name.Trim();
            list.Add(source);
        }

        return list;
    }

    public static SourceDefinition Copy(SourceDefinition source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        Type = source.Type,
        Enabled = source.Enabled,
        CountFilesOnStart = source.CountFilesOnStart,
        Settings = source.Settings?.DeepClone() as JsonObject,
    };
}
