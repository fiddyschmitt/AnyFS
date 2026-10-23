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

    /// <summary>The provider's icon, or null for the default: as <see cref="ISourcePlugin.OpenIcon"/>.</summary>
    Stream? OpenIcon();

    IReadOnlyList<OSPlatform> SupportedPlatforms { get; }

    /// <summary>Why the provider can't run here right now ("WinFsp is not installed. Get it from ..."), or null.</summary>
    string? UnavailableReason { get; }

    PerformanceTier Tier { get; }

    /// <summary>False for protocols that are read-only by nature (plain HTTP).</summary>
    bool SupportsWrites { get; }

    /// <summary>
    /// True while something must be installed with administrator rights before this provider works on this
    /// machine (the WinFsp driver, the fuse3 package). Running the mount itself never needs them. Optional: false.
    /// </summary>
    bool NeedsAdministrator => false;

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors)]
    Type SettingsType { get; }

    object CreateDefaultSettings(ICollectionInfo collection);

    IMount CreateMount(object settings, ICollectionInfo collection);
}

/// <summary>What <see cref="IInstallableProvider.InstallAsync"/> achieved.</summary>
public sealed record InstallResult(bool Success, string Message);

/// <summary>
/// A mount provider that can install what it is missing (WinFsp through winget). The window shows a button
/// with <see cref="InstallText"/> wherever <see cref="IMountProviderPlugin.UnavailableReason"/> is shown; after
/// a successful install the provider is asked again whether it is available.
/// </summary>
public interface IInstallableProvider
{
    /// <summary>"Install WinFsp".</summary>
    string InstallText { get; }

    /// <summary>What the button will do, for the tooltip: "Runs: winget install WinFsp.WinFsp (administrator prompt)".</summary>
    string InstallDetail { get; }

    /// <summary>False when the installer itself isn't available here (no winget).</summary>
    bool CanInstall { get; }

    Task<InstallResult> InstallAsync(IProgress<string> progress, CancellationToken ct);
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

    /// <summary>
    /// Run a command first (a device-less net use), then open <see cref="ThenOpen"/>. <paramref name="Elevated"/>
    /// asks for administrator rights (an elevation prompt on Windows) because the command needs them (starting a service).
    /// <paramref name="Reset"/> is an optional first run of <see cref="FileName"/> whose failure is ignored, used to
    /// clear stale state before the real command: for SMB a <c>net use \\host\share /delete</c>, because Windows caches
    /// the server's credentials and after a password change the reconnect would otherwise fail (net error 1219).
    /// </summary>
    public sealed record Command(string FileName, IReadOnlyList<string> Arguments, string? ThenOpen, bool Elevated = false, IReadOnlyList<string>? Reset = null) : MountOpenAction;

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

    /// <summary>Per-mount state (handle tables, anything kept for this mount alone). Created on first use.</summary>
    string StateDirectory { get; }

    /// <summary>
    /// A cache folder shared by every mount of this provider, for files that can always be made again, such as a helper
    /// program the plugin unpacks: one copy for all its mounts rather than one each. Created on first use.
    /// </summary>
    string ProviderCacheDirectory { get; }

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
