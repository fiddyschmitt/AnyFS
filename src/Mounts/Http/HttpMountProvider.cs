using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Runtime.InteropServices;
using AnyFS.Sdk;

namespace AnyFS.Mounts.Http;

public sealed class HttpMountSettings
{
    [Setting("Bind address", Required = true, Order = 0, Hint = "127.0.0.1 serves this machine only; 0.0.0.0 serves the LAN.")]
    public string BindAddress { get; set; } = "127.0.0.1";

    [Setting("Port", Required = true, Order = 1, Hint = "Mounts on the same address and port share one server.")]
    public int Port { get; set; } = 11001;
}

/// <summary>
/// Plain HTTP (type <c>http</c>, spec 8.6): nginx-style directory pages, <c>?format=json</c> for scripts, and
/// resumable downloads (Accept-Ranges, 206, If-Range against a strong ETag, 416, HEAD). Read-only by nature.
/// One Kestrel per bind address and port, shared by every collection mounted there, at /&lt;collection&gt;/.
/// </summary>
public sealed class HttpMountProvider : IMountProviderPlugin, IValidatable
{
    public const int DefaultPort = 11001;

    public string TypeId => "http";

    public string DisplayName => "HTTP";

    public string Description => "Plain HTTP with directory pages and resumable downloads, for any browser or downloader. Read-only.";

    public Stream? OpenIcon() => null;

    public IReadOnlyList<OSPlatform> SupportedPlatforms { get; } = [OSPlatform.Windows, OSPlatform.Linux, OSPlatform.OSX];

    public string? UnavailableReason => null;

    public PerformanceTier Tier => PerformanceTier.Protocol;

    public bool SupportsWrites => false;

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors)]
    public Type SettingsType => typeof(HttpMountSettings);

    public object CreateDefaultSettings(ICollectionInfo collection) => new HttpMountSettings();

    public IMount CreateMount(object settings, ICollectionInfo collection) => new HttpMount((HttpMountSettings)settings, collection);

    public IEnumerable<ValidationMessage> Validate(object settings)
    {
        var s = (HttpMountSettings)settings;
        if (!TryParseBindAddress(s.BindAddress, out _))
        {
            yield return new ValidationMessage("Use an IP address such as 127.0.0.1, 0.0.0.0 or ::, or 'localhost'.", nameof(HttpMountSettings.BindAddress));
        }

        if (s.Port is < 1 or > 65535)
        {
            yield return new ValidationMessage("The port must be between 1 and 65535.", nameof(HttpMountSettings.Port));
        }
        else if (s.Port < 1024)
        {
            yield return new ValidationMessage("Ports under 1024 need administrator rights on every OS.", nameof(HttpMountSettings.Port), IsError: false);
        }
    }

    public static bool TryParseBindAddress(string? text, out IPAddress address)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case null or "" or "localhost" or "loopback":
                address = IPAddress.Loopback;
                return true;
            case "*" or "any" or "all":
                address = IPAddress.Any;
                return true;
            default:
                return IPAddress.TryParse(text.Trim(), out address!);
        }
    }

    /// <summary>The host name to show in access paths for a bind address.</summary>
    public static string HostFor(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return "localhost";
        }

        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return Environment.MachineName.ToLowerInvariant();
        }

        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
    }
}

internal sealed class HttpMount(HttpMountSettings settings, ICollectionInfo collection) : IMount
{
    private ISharedLease<HttpServer>? _lease;
    private string? _registeredName;

    public IReadOnlyList<AccessPath> AccessPaths { get; private set; } = [];

    public MountOpenAction? OpenAction { get; private set; }

    public async Task StartAsync(IVirtualFileSystem fs, IMountContext context, CancellationToken ct)
    {
        if (!HttpMountProvider.TryParseBindAddress(settings.BindAddress, out var address))
        {
            throw new InvalidOperationException($"'{settings.BindAddress}' is not a valid bind address");
        }

        if (settings.Port is < 1 or > 65535)
        {
            throw new InvalidOperationException($"port {settings.Port} is out of range");
        }

        var key = $"http:{address}:{settings.Port}";
        _lease = await context.AcquireSharedAsync(key, token => HttpServer.StartAsync(address, settings.Port, context.Logger, token), ct).ConfigureAwait(false);
        try
        {
            _lease.Value.Register(collection.Name, fs, context.Stats);
            _registeredName = collection.Name;
        }
        catch
        {
            await _lease.DisposeAsync().ConfigureAwait(false);
            _lease = null;
            throw;
        }

        var url = $"http://{HttpMountProvider.HostFor(address)}:{settings.Port}/{Uri.EscapeDataString(collection.Name)}/";
        AccessPaths = [new AccessPath("URL", url)];
        OpenAction = new MountOpenAction.Browser(url);
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
