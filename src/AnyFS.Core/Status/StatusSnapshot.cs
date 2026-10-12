using AnyFS.Core.Stats;

namespace AnyFS.Core.Status;

/// <summary>
/// What a running <c>anyfs serve</c> writes to state/status.json every few seconds, and what
/// <c>anyfs status</c> and the GUI read. Also the shape returned by the engine in process.
/// </summary>
public sealed class StatusSnapshot
{
    public int Pid { get; set; }

    public string Version { get; set; } = "";

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public string SettingsDirectory { get; set; } = "";

    public List<PluginStatus> Plugins { get; set; } = [];

    public List<CollectionStatus> Collections { get; set; } = [];

    public StatsSnapshot Stats { get; set; } = new();

    /// <summary>True when every enabled component is Ready (exit code 0 for <c>anyfs status</c>).</summary>
    public bool AllGreen => Collections.Where(c => c.Enabled).All(c => c.Colour is nameof(StatusColour.Green) or nameof(StatusColour.Grey));
}

public sealed class PluginStatus
{
    public string TypeId { get; set; } = "";

    /// <summary>"source" or "mount".</summary>
    public string Kind { get; set; } = "";

    public string DisplayName { get; set; } = "";

    /// <summary>"built-in" or the DLL path.</summary>
    public string Origin { get; set; } = "";

    public string? Error { get; set; }
}

public sealed class CollectionStatus
{
    public Guid Id { get; set; }

    public string Name { get; set; } = "";

    public bool Enabled { get; set; }

    public string Colour { get; set; } = "";

    public string Text { get; set; } = "";

    public List<SourceStatus> Sources { get; set; } = [];

    public List<MountStatus> Mounts { get; set; } = [];
}

public sealed class SourceStatus
{
    public Guid Id { get; set; }

    public string Name { get; set; } = "";

    public string Type { get; set; } = "";

    public string Path { get; set; } = "";

    public bool ReadOnly { get; set; }

    public bool Enabled { get; set; }

    public string State { get; set; } = "";

    public string Text { get; set; } = "";

    public double? Progress { get; set; }

    public long Files { get; set; }

    /// <summary>False while the count is a lower bound (shown with a "+" suffix).</summary>
    public bool FilesComplete { get; set; }

    public bool Counting { get; set; }
}

public sealed class MountStatus
{
    public Guid Id { get; set; }

    public string Name { get; set; } = "";

    public string Provider { get; set; } = "";

    public bool Enabled { get; set; }

    public bool ReadOnly { get; set; }

    public string State { get; set; } = "";

    public string Text { get; set; } = "";

    public List<AccessPathStatus> AccessPaths { get; set; } = [];
}

public sealed class AccessPathStatus
{
    public string Label { get; set; } = "";

    public string Value { get; set; } = "";
}
