using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sdk;

public enum PerformanceTier
{
    /// <summary>Kernel file system on the same machine: WinFsp, FUSE.</summary>
    Native,
    /// <summary>Kernel client on this or another machine: SMB, NFS.</summary>
    Network,
    /// <summary>Application protocols: WebDAV, FTP, HTTP.</summary>
    Protocol,
}

public interface ICollectionInfo
{
    Guid Id { get; }

    string Name { get; }
}

/// <summary>A type of mount (WinFsp, SMB, HTTP...). One instance per process; it creates <see cref="IMount"/>s.</summary>
public interface IMountProviderPlugin
{
    /// <summary>Stable id stored in collections.json: "winfsp", "smb", "fuse", "nfs", "webdav", "ftp", "http".</summary>
    string TypeId { get; }

    string DisplayName { get; }

    string Description { get; }

    Stream? OpenIcon();

    IReadOnlyList<OSPlatform> SupportedPlatforms { get; }

    /// <summary>Why the provider can't run here right now ("WinFsp is not installed. Get it from ..."), or null.</summary>
    string? UnavailableReason { get; }

    PerformanceTier Tier { get; }

    /// <summary>False for protocols that are read-only by nature (plain HTTP).</summary>
    bool SupportsWrites { get; }

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors)]
    Type SettingsType { get; }

    object CreateDefaultSettings(ICollectionInfo collection);

    IMount CreateMount(object settings, ICollectionInfo collection);
}

/// <summary>Where a mount can be reached, as shown (and copied) from its tile.</summary>
/// <param name="Label">"URL", "UNC path", "Mount point"...</param>
public sealed record AccessPath(string Label, string Value);

/// <summary>What the Open containing folder button does for this mount.</summary>
public abstract record MountOpenAction
{
    /// <summary>Open a path in the OS file manager.</summary>
    public sealed record FileManager(string Path) : MountOpenAction;

    /// <summary>Open a URL in the default browser (or file manager, for ftp:// and dav://).</summary>
    public sealed record Browser(string Url) : MountOpenAction;

    /// <summary>Run a command first (a device-less net use), then open <see cref="ThenOpen"/>.</summary>
    public sealed record Command(string FileName, IReadOnlyList<string> Arguments, string? ThenOpen) : MountOpenAction;

    /// <summary>Show text with a copy button (a mount command the user must run as root).</summary>
    public sealed record ShowText(string Title, string Text) : MountOpenAction;
}

/// <summary>Connection counting for the Stats tab; files and bytes are counted by the host.</summary>
public interface IMountStatsSink
{
    void ConnectionOpened();

    void ConnectionClosed();
}

/// <summary>A reference-counted shared resource (a listener shared by several mounts on one port).</summary>
public interface ISharedLease<out T> : IAsyncDisposable
    where T : class
{
    T Value { get; }
}

public interface IMountContext
{
    ILogger Logger { get; }

    Guid MountId { get; }

    string MountName { get; }

    ICollectionInfo Collection { get; }

    /// <summary>Per-mount state (extracted binaries, handle tables). Created on first use.</summary>
    string StateDirectory { get; }

    IMountStatsSink Stats { get; }

    /// <summary>Changes the mount's health after <see cref="IMount.StartAsync"/> returned.</summary>
    void ReportHealth(ComponentHealth health, string? detail = null);

    /// <summary>
    /// Gets or creates a resource shared by every mount that asks for the same key (one Kestrel per
    /// bind address and port, one Go server per port). It is disposed when the last lease is.
    /// </summary>
    Task<ISharedLease<T>> AcquireSharedAsync<T>(string key, Func<CancellationToken, Task<T>> factory, CancellationToken ct)
        where T : class, IAsyncDisposable;
}

/// <summary>The collection as a mount sees it: one tree, with the three read-only layers already applied.</summary>
public interface IVirtualFileSystem : IVfsOperations
{
    string CollectionName { get; }

    /// <summary>The mount's own read-only flag, as configured.</summary>
    bool ReadOnly { get; }

    /// <summary>All three layers of read-only for this path (false for synthetic folders and unknown paths).</summary>
    bool CanWrite(string path);
}

/// <summary>A running mount of one collection.</summary>
public interface IMount : IAsyncDisposable
{
    Task StartAsync(IVirtualFileSystem fs, IMountContext context, CancellationToken ct);

    Task StopAsync(CancellationToken ct);

    IReadOnlyList<AccessPath> AccessPaths { get; }

    MountOpenAction? OpenAction { get; }
}
