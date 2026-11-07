namespace AnyFS.Sdk;

/// <summary>
/// What a listings database did since it was opened (<see cref="KeptListings.DiskStatistics"/>): how many of each kind of
/// operation, the time they took in all, and the longest; and how long callers waited for the connection, which one uses
/// at a time. For seeing where a big source's time goes.
/// </summary>
public sealed record ListingStoreStatistics
{
    /// <summary>Listings asked for by path.</summary>
    public required ListingStoreOperation Reads { get; init; }

    /// <summary>Of the reads, how many found a listing.</summary>
    public required long ReadsFound { get; init; }

    /// <summary>Listings stored for a folder that had none.</summary>
    public required ListingStoreOperation Inserts { get; init; }

    /// <summary>Listings stored in place of one held.</summary>
    public required ListingStoreOperation Updates { get; init; }

    /// <summary>Listings let go.</summary>
    public required ListingStoreOperation Deletes { get; init; }

    /// <summary>Pages of the listings below a folder read in path order.</summary>
    public required ListingStoreOperation Ranges { get; init; }

    /// <summary>The rows those pages brought.</summary>
    public required long RangeRows { get; init; }

    /// <summary>Commits: everything stored since the last one on the disk.</summary>
    public required ListingStoreOperation Commits { get; init; }

    /// <summary>The bytes of the listings stored.</summary>
    public required long BytesWritten { get; init; }

    /// <summary>How long callers waited for the connection in all.</summary>
    public required TimeSpan Waited { get; init; }

    /// <summary>The longest one caller waited for the connection.</summary>
    public required TimeSpan LongestWait { get; init; }
}

/// <summary>One kind of operation of a listings database: how many, their time in all, and the longest.</summary>
public readonly record struct ListingStoreOperation(long Count, TimeSpan Total, TimeSpan Longest)
{
    /// <summary>The time one took on average; zero where there were none.</summary>
    public TimeSpan Mean => Count == 0 ? TimeSpan.Zero : Total / Count;
}
