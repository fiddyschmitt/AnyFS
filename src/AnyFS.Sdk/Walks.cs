namespace AnyFS.Sdk;

/// <summary>
/// Whether the code asking is at work for a walk of the source's own (a count, a walk of the kept listings: no one waits
/// on any one of its requests) rather than for a client that does (a program listing a folder or opening a file). Set for
/// the walks' workers by <see cref="KeptListings"/> and <see cref="TreeCount"/>, and read by a source that gates its requests
/// to let a client's go first, and by the host's Operations tab to say what a request of the server was for.
/// </summary>
public static class Walks
{
    private static readonly AsyncLocal<bool> InWalk = new();

    /// <summary>True on the walks' threads and all they start; false for a client's call.</summary>
    public static bool Current
    {
        get => InWalk.Value;
        set => InWalk.Value = value;
    }
}
