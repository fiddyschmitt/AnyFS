using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Runtime.InteropServices;
using AnyFS.Sdk;

namespace AnyFS.Mounts.Ftp;

public sealed class FtpMountSettings
{
    [Setting("Bind address", Required = true, Order = 0, Hint = "127.0.0.1 serves this machine only; 0.0.0.0 serves the LAN.")]
    public string BindAddress { get; set; } = "127.0.0.1";

    [Setting("Port", Required = true, Order = 1, Hint = "Mounts on the same address and port share one server.")]
    public int Port { get; set; } = FtpMountProvider.DefaultPort;

    [Setting("Allow anonymous", Order = 2, Group = "Authentication", Hint = "Log in as 'anonymous' with any password.")]
    public bool AllowAnonymous { get; set; } = true;

    [Setting("User name", Order = 3, Group = "Authentication", Hint = "A named login that sees this collection; leave empty for anonymous only.")]
    public string? User { get; set; }

    [Setting("Password", Secret = true, Order = 4, Group = "Authentication")]
    public string? Password { get; set; }

    [Setting("Passive ports from", Advanced = true, Order = 10, Hint = "First port of the passive-mode range (empty: any free port). Set a range when a firewall is in the way.")]
    public int? PassivePortFrom { get; set; }

    [Setting("Passive ports to", Advanced = true, Order = 11)]
    public int? PassivePortTo { get; set; }

    [Setting("Public address", Advanced = true, Order = 12, Hint = "The address clients should connect back to in passive mode, when the server is behind NAT (empty: the address they connected to).")]
    public string? PublicAddress { get; set; }
}

/// <summary>
/// FTP (type <c>ftp</c>, spec 8.5) through AnyFS's own server (<see cref="FtpServer"/>): one server per bind address
/// and port, every collection a folder under its root, resume (REST), passive mode with an optional port range,
/// active mode, anonymous or user/password. Explorer, Finder (read-only) and xdg-open all open ftp:// URLs, so
/// nothing is mapped (spec 8.9).
/// </summary>
public sealed class FtpMountProvider : IMountProviderPlugin, IValidatable
{
    public const int DefaultPort = 2121;

    public string TypeId => "ftp";

    public string DisplayName => "FTP";

    public string Description => "FTP with resume and passive mode, for Explorer, Finder (read-only), file managers and ftp clients.";

    public Stream? OpenIcon() => null;

    public IReadOnlyList<OSPlatform> SupportedPlatforms { get; } = [OSPlatform.Windows, OSPlatform.Linux, OSPlatform.OSX];

    public string? UnavailableReason => null;

    public PerformanceTier Tier => PerformanceTier.Protocol;

    public bool SupportsWrites => true;

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors)]
    public Type SettingsType => typeof(FtpMountSettings);

    public object CreateDefaultSettings(ICollectionInfo collection) => new FtpMountSettings();

    public IMount CreateMount(object settings, ICollectionInfo collection) => new FtpMount((FtpMountSettings)settings, collection);

    public IEnumerable<ValidationMessage> Validate(object settings)
    {
        var s = (FtpMountSettings)settings;
        foreach (var message in BindAddress.Validate(s.BindAddress, s.Port, nameof(FtpMountSettings.BindAddress), nameof(FtpMountSettings.Port)))
        {
            yield return message;
        }

        var hasUser = !string.IsNullOrWhiteSpace(s.User);
        if (hasUser && string.IsNullOrEmpty(s.Password))
        {
            yield return new ValidationMessage("Give the user a password.", nameof(FtpMountSettings.Password));
        }

        if (!hasUser && !s.AllowAnonymous)
        {
            yield return new ValidationMessage("Allow anonymous logins or set a user name, or nobody can log in.", nameof(FtpMountSettings.AllowAnonymous));
        }

        if (s.AllowAnonymous && BindAddress.TryParse(s.BindAddress, out var address) && BindAddress.IsLanExposed(address))
        {
            yield return new ValidationMessage("Anonymous logins on a LAN address: anyone on the network can read (and write) the collection.", nameof(FtpMountSettings.AllowAnonymous), IsError: false);
        }

        if ((s.PassivePortFrom is null) != (s.PassivePortTo is null))
        {
            yield return new ValidationMessage("Set both ends of the passive port range, or neither.", nameof(FtpMountSettings.PassivePortTo));
        }
        else if (s.PassivePortFrom is { } from && s.PassivePortTo is { } to && (from < 1024 || to > 65535 || from > to))
        {
            yield return new ValidationMessage("The passive range must lie between 1024 and 65535, lowest first.", nameof(FtpMountSettings.PassivePortFrom));
        }

        if (!string.IsNullOrWhiteSpace(s.PublicAddress) && !IPAddress.TryParse(s.PublicAddress.Trim(), out _))
        {
            yield return new ValidationMessage("The public address must be an IP address.", nameof(FtpMountSettings.PublicAddress));
        }
    }
}

internal sealed class FtpMount(FtpMountSettings settings, ICollectionInfo collection) : IMount
{
    private ISharedLease<FtpServer>? _lease;
    private string? _registeredName;

    public IReadOnlyList<AccessPath> AccessPaths { get; private set; } = [];

    public MountOpenAction? OpenAction { get; private set; }

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

        var user = string.IsNullOrWhiteSpace(settings.User) ? null : settings.User.Trim();
        if (user is null && !settings.AllowAnonymous)
        {
            throw new InvalidOperationException("no login is allowed: allow anonymous or set a user name");
        }

        var passive = new FtpPassiveOptions(settings.PassivePortFrom, settings.PassivePortTo, IPAddress.TryParse(settings.PublicAddress?.Trim(), out var publicAddress) ? publicAddress : null);
        var key = $"ftp:{address}:{settings.Port}";
        _lease = await context.AcquireSharedAsync(key, token => FtpServer.StartAsync(address, settings.Port, passive, context.Logger, token), ct).ConfigureAwait(false);
        try
        {
            _lease.Value.Register(new FtpCollection(collection.Name, fs, context.Stats, user, settings.Password, settings.AllowAnonymous));
            _registeredName = collection.Name;
        }
        catch
        {
            await _lease.DisposeAsync().ConfigureAwait(false);
            _lease = null;
            throw;
        }

        var host = BindAddress.HostFor(address);
        var url = $"ftp://{(user is null ? "" : Uri.EscapeDataString(user) + "@")}{host}:{settings.Port}/{Uri.EscapeDataString(collection.Name)}/";
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
