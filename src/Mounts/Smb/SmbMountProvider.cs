using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using AnyFS.Sdk;

namespace AnyFS.Mounts.Smb;

public sealed class SmbMountSettings
{
    [Setting("Bind address", Required = true, Order = 0, Hint = "127.0.0.1 serves this machine only; 0.0.0.0 serves the LAN.")]
    public string BindAddress { get; set; } = "127.0.0.1";

    [Setting("Port", Required = true, Order = 1, Hint = "1445 needs no administrator rights and doesn't clash with Windows' own SMB on 445. Collections on the same address and port share one server.")]
    public int Port { get; set; } = SmbMountProvider.DefaultPort;

    [Setting("Guest access", Order = 2, Group = "Authentication", Hint = "No password at all. Windows refuses guest shares unless its EnableInsecureGuestLogons policy is on, so a user and password is the usual choice.")]
    public bool Guest { get; set; }

    [Setting("User name", Order = 3, Group = "Authentication", Hint = "Every collection on the same port uses the same login.")]
    public string? User { get; set; } = "user";

    [Setting("Password", Secret = true, Order = 4, Group = "Authentication")]
    public string? Password { get; set; }
}

/// <summary>
/// SMB (type <c>smb</c>, spec 8.2) through portable-smb-server: one Go process per bind address and port serving every
/// collection mounted there, fed by a loopback VFS endpoint protected by a per-launch token. Reached with a device-less
/// <c>net use \\host\collection /TCPPORT:1445</c> on Windows 11, <c>mount -t cifs</c> on Linux and Finder's
/// <c>smb://host:1445/collection</c> on macOS; no drive is ever mapped (spec 8.9).
/// </summary>
public sealed class SmbMountProvider : IMountProviderPlugin, IValidatable
{
    public const int DefaultPort = 1445;

    public string TypeId => "smb";

    public string DisplayName => "SMB";

    public string Description => "A Windows-style file share (SMB 2/3) for this machine and the LAN, through the bundled portable-smb-server. Explorer, Finder and Linux mount it.";

    public Stream? OpenIcon() => null;

    public IReadOnlyList<OSPlatform> SupportedPlatforms { get; } = [OSPlatform.Windows, OSPlatform.Linux, OSPlatform.OSX];

    public string? UnavailableReason => SmbServerBinary.IsAvailable(out var reason) ? null : reason;

    public PerformanceTier Tier => PerformanceTier.Network;

    public bool SupportsWrites => true;

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors)]
    public Type SettingsType => typeof(SmbMountSettings);

    public object CreateDefaultSettings(ICollectionInfo collection) => new SmbMountSettings();

    public IMount CreateMount(object settings, ICollectionInfo collection) => new SmbMount((SmbMountSettings)settings, collection);

    public IEnumerable<ValidationMessage> Validate(object settings)
    {
        var s = (SmbMountSettings)settings;
        foreach (var message in BindAddress.Validate(s.BindAddress, s.Port, nameof(SmbMountSettings.BindAddress), nameof(SmbMountSettings.Port)))
        {
            yield return message;
        }

        if (s.Port == 445)
        {
            yield return new ValidationMessage("Port 445 is held by Windows' own file sharing on Windows and needs administrator rights elsewhere; 1445 is the usual choice.", nameof(SmbMountSettings.Port), IsError: false);
        }

        if (!s.Guest)
        {
            if (string.IsNullOrWhiteSpace(s.User))
            {
                yield return new ValidationMessage("Give the share a user name, or turn on guest access.", nameof(SmbMountSettings.User));
            }

            if (string.IsNullOrEmpty(s.Password))
            {
                yield return new ValidationMessage("Give the user a password, or turn on guest access.", nameof(SmbMountSettings.Password));
            }
        }
        else if (BindAddress.TryParse(s.BindAddress, out var address) && BindAddress.IsLanExposed(address))
        {
            yield return new ValidationMessage("Guest access on a LAN address: anyone on the network can read (and write) the collection.", nameof(SmbMountSettings.Guest), IsError: false);
        }
    }

    /// <summary>The device-less connection Windows 11 24H2+ needs for a port other than 445 (spike 2): registers the port for that server, then the UNC path just works.</summary>
    public static IReadOnlyList<string> WindowsConnectArguments(string unc, int port, SmbCredentialsView credentials)
    {
        var args = new List<string> { "use", unc, $"/TCPPORT:{port}" };
        if (!credentials.Guest)
        {
            args.Add($"/USER:{credentials.User}");
            args.Add(credentials.Password ?? "");
        }

        return args;
    }

    public static string LinuxMountCommand(string host, int port, string collection, SmbCredentialsView credentials)
        => credentials.Guest
            ? $"sudo mount -t cifs //{host}/{collection} /mnt/{collection} -o port={port},vers=3.0,guest"
            : $"sudo mount -t cifs //{host}/{collection} /mnt/{collection} -o port={port},vers=3.0,user={credentials.User}";

    public static string MacUrl(string host, int port, string collection, SmbCredentialsView credentials)
        => credentials.Guest ? $"smb://guest@{host}:{port}/{Uri.EscapeDataString(collection)}" : $"smb://{Uri.EscapeDataString(credentials.User ?? "")}@{host}:{port}/{Uri.EscapeDataString(collection)}";
}

/// <summary>The login as shown in access paths (the password never appears in them).</summary>
public sealed record SmbCredentialsView(string? User, string? Password, bool Guest);

internal sealed class SmbMount(SmbMountSettings settings, ICollectionInfo collection) : IMount
{
    private ISharedLease<SmbServerProcess>? _lease;
    private string? _registeredName;
    private Action<string>? _onDied;

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

        var credentials = new SmbCredentials(settings.Guest ? null : settings.User?.Trim(), settings.Guest ? null : settings.Password, settings.Guest);
        if (!settings.Guest && (string.IsNullOrEmpty(credentials.User) || string.IsNullOrEmpty(credentials.Password)))
        {
            throw new InvalidOperationException("the share needs a user name and password, or guest access");
        }

        var binary = SmbServerBinary.Locate(context.StateDirectory);
        var key = $"smb:{address}:{settings.Port}";
        _lease = await context.AcquireSharedAsync(key, token => SmbServerProcess.StartAsync(address, settings.Port, binary, context.Logger, token), ct).ConfigureAwait(false);
        try
        {
            await _lease.Value.RegisterAsync(collection.Name, fs, context.Stats, credentials, ct).ConfigureAwait(false);
            _registeredName = collection.Name;
        }
        catch
        {
            await _lease.DisposeAsync().ConfigureAwait(false);
            _lease = null;
            throw;
        }

        _onDied = message => context.ReportHealth(ComponentHealth.Failed, message);
        _lease.Value.Died += _onDied;

        var host = BindAddress.HostFor(address);
        var view = new SmbCredentialsView(credentials.User, null, credentials.Guest);
        var unc = $@"\\{host}\{collection.Name}";
        var paths = new List<AccessPath>();
        if (OperatingSystem.IsWindows())
        {
            paths.Add(new AccessPath("UNC path", unc));
            paths.Add(new AccessPath("Connect command", "net " + string.Join(' ', SmbMountProvider.WindowsConnectArguments(unc, settings.Port, view).Select(a => a.Contains(' ') ? '"' + a + '"' : a)).TrimEnd()));
            OpenAction = new MountOpenAction.Command("net", SmbMountProvider.WindowsConnectArguments(unc, settings.Port, new SmbCredentialsView(credentials.User, credentials.Password, credentials.Guest)), unc);
        }
        else if (OperatingSystem.IsMacOS())
        {
            var url = SmbMountProvider.MacUrl(host, settings.Port, collection.Name, view);
            paths.Add(new AccessPath("URL", url));
            paths.Add(new AccessPath("Mount command", $"mount -t smbfs //{(credentials.Guest ? "guest" : credentials.User)}@{host}:{settings.Port}/{collection.Name} ~/anyfs/{collection.Name}"));
            OpenAction = new MountOpenAction.Browser(url);
        }
        else
        {
            paths.Add(new AccessPath("UNC path", $"//{host}/{collection.Name}"));
            var command = SmbMountProvider.LinuxMountCommand(host, settings.Port, collection.Name, view);
            paths.Add(new AccessPath("Mount command", command));
            OpenAction = new MountOpenAction.ShowText($"Mount {collection.Name}", command + (credentials.Guest ? "" : "\n(it will ask for the password)"));
        }

        AccessPaths = paths;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_lease is not null && _onDied is not null)
        {
            _lease.Value.Died -= _onDied;
            _onDied = null;
        }

        if (_lease is not null && _registeredName is not null)
        {
            await _lease.Value.UnregisterAsync(_registeredName).ConfigureAwait(false);
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
