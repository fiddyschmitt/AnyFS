namespace AnyFS.Core.Operations;

/// <summary>What was asked of a source: one of its calls, or its start, its count of files and its stop.</summary>
public enum OperationKind
{
    /// <summary><see cref="Sdk.ISource.InitialiseAsync"/>: logging in, opening the connection.</summary>
    Start,
    /// <summary><see cref="Sdk.IVfsOperations.StatAsync"/>.</summary>
    Stat,
    /// <summary><see cref="Sdk.IVfsOperations.ListAsync"/>, from the first entry asked for to the last given.</summary>
    List,
    /// <summary>A file opened for reading, from the open until the handle is closed; the bytes read go by on it.</summary>
    ReadFile,
    /// <summary>A file opened for writing (or created), from the open until the handle is closed, uploads on close included.</summary>
    WriteFile,
    CreateDirectory,
    Delete,
    Rename,
    SetTimes,
    GetSpace,
    /// <summary>"Count files on start": the source's own count or the host's walk of its tree.</summary>
    Count,
    /// <summary>One folder a source that counts itself is going through for its count (<see cref="Sdk.SourceCounters.EnterFolder"/>).</summary>
    CountFolder,
    /// <summary><see cref="IAsyncDisposable.DisposeAsync"/> of the source.</summary>
    Stop,
}

public enum OperationOutcome
{
    Running,
    Succeeded,
    Failed,
    /// <summary>The caller gave up (its token was cancelled, or it stopped reading a listing before the end).</summary>
    Cancelled,
}

public static class OperationKindExtensions
{
    /// <summary>The words the window's Operations tab and <c>anyfs status --operations</c> use.</summary>
    public static string Describe(this OperationKind kind) => kind switch
    {
        OperationKind.Start => "Start",
        OperationKind.Stat => "Get metadata",
        OperationKind.List => "List folder",
        OperationKind.ReadFile => "Read file",
        OperationKind.WriteFile => "Write file",
        OperationKind.CreateDirectory => "Create folder",
        OperationKind.Delete => "Delete",
        OperationKind.Rename => "Rename",
        OperationKind.SetTimes => "Set times",
        OperationKind.GetSpace => "Get space",
        OperationKind.Count => "Count files",
        OperationKind.CountFolder => "Count folder",
        OperationKind.Stop => "Stop",
        _ => kind.ToString(),
    };

    /// <summary>Whether the operation is about a path in the source (its start, count and stop are not).</summary>
    public static bool HasPath(this OperationKind kind) => kind is not (OperationKind.Start or OperationKind.Count or OperationKind.Stop);
}
