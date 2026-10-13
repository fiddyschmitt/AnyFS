using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using AnyFS.Sdk;

namespace AnyFS.Mounts.WebDav;

public sealed class WebDavMountSettings
{
    [Setting("Bind address", Required = true, Order = 0, Hint = "127.0.0.1 serves this machine only; 0.0.0.0 serves the LAN.")]
    public string BindAddress { get; set; } = "127.0.0.1";

    [Setting("Port", Required = true, Order = 1, Hint = "Mounts on the same address and port share one server.")]
    public int Port { get; set; } = WebDavMountProvider.DefaultPort;

    [Setting("User name", Order = 2, Group = "Authentication", Hint = "Leave empty for no authentication (fine on 127.0.0.1). Windows refuses Basic authentication over plain HTTP unless BasicAuthLevel is 2.")]
    public string? User { get; set; }

    [Setting("Password", Secret = true, Order = 3, Group = "Authentication")]
    public string? Password { get; set; }
}

/// <summary>
/// WebDAV (type <c>webdav</c>, spec 8.4): our own minimal RFC 4918 server on Kestrel, one per bind address and port,
/// every collection at /&lt;collection&gt;/. Reached on Windows as \\host@port\DavWWWRoot\collection (the WebClient
/// service), on macOS through Finder, on Linux through gvfs/kio or davfs2. Nothing is mapped (spec 8.9).
/// </summary>
public sealed class WebDavMountProvider : IMountProviderPlugin, IValidatable
{
    public const int DefaultPort = 11000;

    public string TypeId => "webdav";

    public string DisplayName => "WebDAV";

    public string Description => "WebDAV for Explorer (\\\\host@port\\DavWWWRoot), Finder, GNOME, KDE and davfs2. Windows caps files at 50 MB; locks are kept in memory.";

    public Stream? OpenIcon() => null;

    public IReadOnlyList<OSPlatform> SupportedPlatforms { get; } = [OSPlatform.Windows, OSPlatform.Linux, OSPlatform.OSX];

    public string? UnavailableReason => null;

    public PerformanceTier Tier => PerformanceTier.Protocol;

    public bool SupportsWrites => true;

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors)]
    public Type SettingsType => typeof(WebDavMountSettings);

    public object CreateDefaultSettings(ICollectionInfo collection) => new WebDavMountSettings();

    public IMount CreateMount(object settings, ICollectionInfo collection) => new WebDavMount((WebDavMountSettings)settings, collection);

    public IEnumerable<ValidationMessage> Validate(object settings)
    {
        var s = (WebDavMountSettings)settings;
        foreach (var message in BindAddress.Validate(s.BindAddress, s.Port, nameof(WebDavMountSettings.BindAddress), nameof(WebDavMountSettings.Port)))
        {
            yield return message;
        }

        var hasUser = !string.IsNullOrWhiteSpace(s.User);
        if (hasUser && string.IsNullOrEmpty(s.Password))
        {
            yield return new ValidationMessage("Give the user a password, or clear the user name for no authentication.", nameof(WebDavMountSettings.Password));
        }

        if (!hasUser && BindAddress.TryParse(s.BindAddress, out var address) && BindAddress.IsLanExposed(address))
        {
            yield return new ValidationMessage("This serves the LAN with no password: anyone on the network can read (and write) the collection.", nameof(WebDavMountSettings.User), IsError: false);
        }
    }

    /// <summary>The Windows form the WebDAV redirector understands: the @port and DavWWWRoot keywords keep it off SMB (spec 8.4).</summary>
    public static string WindowsUncPath(string host, int port, string collection) => $@"\\{host}@{port}\DavWWWRoot\{collection}";
}

internal sealed class WebDavMount(WebDavMountSettings settings, ICollectionInfo collection) : IMount
{
    private ISharedLease<WebDavServer>? _lease;
    private string? _registeredName;

    public IReadOnlyList<AccessPath> AccessPaths { get; private set; } = [];

    public MountOpenAction? OpenAction { get; private set; }

    /// <summary>Live locks on the shared server (tests).</summary>
    internal int LockCount => _lease?.Value.Locks.Count ?? 0;

    public async Task StartAsync(IVirtualFileSystem fs, IMountContext context, CancellationToken ct)
    {
        if (!BindAddress.TryParse(settings.BindAddress, out var address))
        {
            throw new InvalidOperationException($"'{settings.BindAddress}' is not a valid bind address");
        }

        if (settings.Port is < 1 or > 65535)
        {
            throw new InvalidOperationException($"port {settings.Port} is out of range");
        }

        var credentials = string.IsNullOrWhiteSpace(settings.User) ? null : new DavCredentials(settings.User.Trim(), settings.Password ?? "");
        var key = $"webdav:{address}:{settings.Port}";
        _lease = await context.AcquireSharedAsync(key, token => WebDavServer.StartAsync(address, settings.Port, context.Logger, token), ct).ConfigureAwait(false);
        try
        {
            _lease.Value.Register(collection.Name, fs, context.Stats, credentials);
            _registeredName = collection.Name;
        }
        catch
        {
            await _lease.DisposeAsync().ConfigureAwait(false);
            _lease = null;
            throw;
        }

        var host = BindAddress.HostFor(address);
        var url = $"http://{host}:{settings.Port}/{Uri.EscapeDataString(collection.Name)}/";
        var paths = new List<AccessPath> { new("URL", url) };
        if (OperatingSystem.IsWindows())
        {
            var unc = WebDavMountProvider.WindowsUncPath(host, settings.Port, collection.Name);
            paths.Add(new AccessPath("UNC path", unc));
            if (WindowsServices.IsRunning("WebClient"))
            {
                OpenAction = new MountOpenAction.FileManager(unc);
            }
            else
            {
                // spec 8.4 / spike 5: the WebClient service does not start on first use; Open starts it (elevation prompt) then opens the path
                OpenAction = new MountOpenAction.Command("sc.exe", ["start", "WebClient"], unc, Elevated: true);
                context.ReportHealth(ComponentHealth.Degraded, "The WebClient service is not running, so Explorer can't open the UNC path yet. Open starts it (administrator prompt); to make it start on its own: sc config WebClient start= auto");
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            // Finder mounts it under /Volumes/<collection>; nothing is mapped (spec 8.9)
            OpenAction = new MountOpenAction.Command("osascript", ["-e", $"mount volume \"{url}\""], $"/Volumes/{collection.Name}");
        }
        else
        {
            OpenAction = new MountOpenAction.Browser($"dav://{host}:{settings.Port}/{Uri.EscapeDataString(collection.Name)}/");
        }

        AccessPaths = paths;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_lease is not null && _registeredName is not null)
        {
            _lease.Value.Unregister(_registeredName);
            _registeredName = null;
        }

        if (_lease is not null)
        {
            await _lease.DisposeAsync().ConfigureAwait(false);
            _lease = null;
        }

        AccessPaths = [];
        OpenAction = null;
    }

    public ValueTask DisposeAsync() => new(StopAsync(CancellationToken.None));
}

/// <summary>Windows service state through sc.exe, so no Windows-only assembly is needed.</summary>
internal static class WindowsServices
{
    public static bool IsRunning(string service)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            var info = new ProcessStartInfo("sc.exe") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            info.ArgumentList.Add("query");
            info.ArgumentList.Add(service);
            using var process = Process.Start(info);
            if (process is null)
            {
                return false;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return output.Contains("RUNNING", StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return false;
        }
    }
}
