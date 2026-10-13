using System.Text.Json.Nodes;

namespace AnyFS.Core.Model;

/// <summary>One entry in sources.json: a configured source, usable by any number of collections.</summary>
public sealed class SourceDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "";

    /// <summary>The plugin's <c>TypeId</c>: "mirror", "sshfs".</summary>
    public string Type { get; set; } = "";

    public bool Enabled { get; set; } = true;

    /// <summary>Walk the whole tree in the background at start so the count is exact (section 6.8).</summary>
    public bool CountFilesOnStart { get; set; }

    /// <summary>The plugin's settings POCO as JSON, minus secret properties.</summary>
    public JsonObject? Settings { get; set; }
}

/// <summary>A source's membership of a collection: where it sits and whether this collection may write to it.</summary>
public sealed class SourceMembership
{
    public Guid Id { get; set; }

    /// <summary>The source path, '/'-separated, may be empty (the source is the collection's root).</summary>
    public string Path { get; set; } = "";

    public bool ReadOnly { get; set; }
}

public sealed class MountDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "";

    /// <summary>The provider's <c>TypeId</c>: "winfsp", "smb", "fuse", "nfs", "webdav", "ftp", "http".</summary>
    public string Provider { get; set; } = "";

    public bool Enabled { get; set; } = true;

    /// <summary>Everything through this mount is read-only (the third layer of section 4.2).</summary>
    public bool ReadOnly { get; set; }

    public JsonObject? Settings { get; set; }
}

public sealed class CollectionDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "";

    public bool Enabled { get; set; } = true;

    public List<SourceMembership> Sources { get; set; } = [];

    public List<MountDefinition> Mounts { get; set; } = [];
}

public sealed class WindowPlacement
{
    public int X { get; set; }

    public int Y { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public bool Maximised { get; set; }
}

/// <summary>settings/app.json.</summary>
public sealed class AppSettings
{
    public int Version { get; set; } = SettingsFormat.CurrentVersion;

    /// <summary>trace, debug, info, warn or error.</summary>
    public string LogLevel { get; set; } = "info";

    /// <summary>Default bind address offered to new network mounts.</summary>
    public string BindAddress { get; set; } = "127.0.0.1";

    /// <summary>system, light or dark.</summary>
    public string Theme { get; set; } = "system";

    /// <summary>The default for new sources: null follows the source type (its plugin's IsQuickToCount), true or false forces it.</summary>
    public bool? CountFilesOnStart { get; set; }

    public WindowPlacement? Window { get; set; }
}

/// <summary>settings/sources.json: the file people share.</summary>
public sealed class SourcesFile
{
    public int Version { get; set; } = SettingsFormat.CurrentVersion;

    public List<SourceDefinition> Sources { get; set; } = [];
}

/// <summary>settings/collections.json.</summary>
public sealed class CollectionsFile
{
    public int Version { get; set; } = SettingsFormat.CurrentVersion;

    public List<CollectionDefinition> Collections { get; set; } = [];
}

/// <summary>state/secrets.json: protected values keyed "&lt;owner id&gt;/&lt;property&gt;".</summary>
public sealed class SecretsFile
{
    public int Version { get; set; } = SettingsFormat.CurrentVersion;

    public Dictionary<string, string> Secrets { get; set; } = new(StringComparer.Ordinal);
}

public static class SettingsFormat
{
    public const int CurrentVersion = 1;
}
