using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AnyFS.Sdk;
using Fsp;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace AnyFS.Mounts.WinFsp;

public sealed class WinFspMountSettings
{
    [Setting("Drive letter", Advanced = true, Order = 0, Hint = "Also map a drive letter (X) for programs that can't open UNC paths. Leave empty: \\\\anyfs\\<collection> needs no letter (spec 8.9).")]
    public string? DriveLetter { get; set; }
}

/// <summary>
/// WinFsp (type <c>winfsp</c>, spec 8.1): one WinFsp network file system per collection with the prefix
/// \anyfs\&lt;collection&gt;, so \\anyfs\&lt;collection&gt; works in Explorer, cmd and every program with no DNS and no
/// drive letter (a letter is opt-in; Open uses it when set). The WinFsp runtime is a driver installed separately; the
/// provider notices its absence and offers to install it, silently from the bundled installer (<see cref="WinFspInstaller"/>)
/// or through winget when a build has none (N6).
/// </summary>
public sealed class WinFspMountProvider : IMountProviderPlugin, IValidatable, IInstallableProvider
{
    public const string WingetId = "WinFsp.WinFsp";

    /// <summary>Before any mount touches winfsp-msil: a single-file host has to be handed it as a file (<see cref="WinFspInterop"/>).</summary>
    static WinFspMountProvider() => WinFspInterop.Register();

    public string TypeId => "winfsp";

    public string DisplayName => "WinFsp";

    public string Description => "A native file system for this machine: \\\\anyfs\\<collection> in Explorer and every program, with the best performance. Needs the WinFsp driver.";

    public Stream? OpenIcon() => null;

    public IReadOnlyList<OSPlatform> SupportedPlatforms { get; } = [OSPlatform.Windows];

    /// <summary>No driver, or one this build's WinFsp layer refuses (an older minor or another major): the tile shows it with the Install/Upgrade button.</summary>
    public string? UnavailableReason => !OperatingSystem.IsWindows() ? "WinFsp is a Windows driver" : WinFspRuntime.Problem();

    public PerformanceTier Tier => PerformanceTier.Native;

    public bool SupportsWrites => true;

    /// <summary>The driver install (or upgrade) is the one step that needs an administrator; the mount itself doesn't.</summary>
    public bool NeedsAdministrator => OperatingSystem.IsWindows() && WinFspRuntime.Problem() is not null;

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors)]
    public Type SettingsType => typeof(WinFspMountSettings);

    public object CreateDefaultSettings(ICollectionInfo collection) => new WinFspMountSettings();

    public IMount CreateMount(object settings, ICollectionInfo collection) => new WinFspMount((WinFspMountSettings)settings, collection);

    public IEnumerable<ValidationMessage> Validate(object settings)
    {
        var s = (WinFspMountSettings)settings;
        if (DriveLetter.Validate(s.DriveLetter, nameof(WinFspMountSettings.DriveLetter)) is { } message)
        {
            yield return message;
        }
    }

    public string InstallText => OperatingSystem.IsWindows() && WinFspRuntime.IsInstalled() ? "Upgrade WinFsp driver" : "Install WinFsp driver";

    public string InstallDetail => WinFspInstaller.HasEmbedded()
        ? $"Installs the bundled WinFsp {WinFspInstaller.Version} driver with no installer windows (an administrator prompt appears; no reboot)"
        : $"Runs: winget install --id {WingetId} (an administrator prompt appears for the installer; no reboot)";

    public bool CanInstall => OperatingSystem.IsWindows() && (WinFspInstaller.HasEmbedded() || WinFspRuntime.FindWinget() is not null);

    public async Task<InstallResult> InstallAsync(IProgress<string> progress, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new InstallResult(false, "WinFsp is a Windows driver.");
        }

        if (WinFspInstaller.HasEmbedded())
        {
            return await WinFspInstaller.InstallAsync(progress, ct).ConfigureAwait(false);
        }

        var winget = WinFspRuntime.FindWinget();
        if (winget is null)
        {
            return new InstallResult(false, "winget was not found. Install WinFsp from https://winfsp.dev instead.");
        }

        progress.Report($"Running winget install {WingetId}; approve the administrator prompt when it appears.");
        var info = new ProcessStartInfo(winget) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { "install", "--id", WingetId, "--exact", "--source", "winget", "--accept-source-agreements", "--accept-package-agreements", "--disable-interactivity" })
        {
            info.ArgumentList.Add(argument);
        }

        var tail = new List<string>();
        using var process = Process.Start(info) ?? throw new InvalidOperationException("winget did not start");
        process.OutputDataReceived += (_, e) => Report(e.Data);
        process.ErrorDataReceived += (_, e) => Report(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        if (!WinFspRuntime.IsInstalled())
        {
            return new InstallResult(false, $"winget exited with code {process.ExitCode} and WinFsp is still missing: {string.Join(" | ", tail)}");
        }

        // winget carries the stable WinFsp (2.1), older than the 2026 beta this build links
        return WinFspRuntime.Problem() is { } problem
            ? new InstallResult(false, $"{problem} winget installs the stable WinFsp only; install WinFsp {WinFspInstaller.Version} from https://github.com/winfsp/winfsp/releases.")
            : new InstallResult(true, "WinFsp is installed. The mount can start now.");

        void Report(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            progress.Report(line.Trim());
            lock (tail)
            {
                tail.Add(line.Trim());
                if (tail.Count > 6)
                {
                    tail.RemoveAt(0);
                }
            }
        }
    }

    public static string? NormaliseLetter(string? text) => DriveLetter.Normalise(text);
}

internal sealed class WinFspMount(WinFspMountSettings settings, ICollectionInfo collection) : IMount
{
    private FileSystemHost? _host;

    public IReadOnlyList<AccessPath> AccessPaths { get; private set; } = [];

    public MountOpenAction? OpenAction { get; private set; }

    public Task StartAsync(IVirtualFileSystem fs, IMountContext context, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new InvalidOperationException("WinFsp is a Windows driver");
            }

            try
            {
                Mount(fs, context);
            }
            catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
            {
                // the .NET layer itself (winfsp-msil) did not load: Mount is the first method that needs it
                throw new InvalidOperationException(WinFspRuntime.Explain(ex), ex);
            }
        }, ct);
    }

    [SupportedOSPlatform("windows")]
    private void Mount(IVirtualFileSystem fs, IMountContext context)
    {
        var letter = WinFspMountProvider.NormaliseLetter(settings.DriveLetter);
        var prefix = $@"\anyfs\{collection.Name}";
        var host = new FileSystemHost(new WinFspFileSystem(fs, collection.Name, prefix, context.Logger))
        {
            Prefix = prefix,
            FileSystemName = "AnyFS",
            SectorSize = 4096,
            SectorsPerAllocationUnit = 1,
            MaxComponentLength = 255,
            FileInfoTimeout = 1000,
            CaseSensitiveSearch = false,
            CasePreservedNames = true,
            UnicodeOnDisk = true,
            PersistentAcls = false,
            PostCleanupWhenModifiedOnly = true,
            PassQueryDirectoryPattern = false,
            FlushAndPurgeOnCleanup = false,
            VolumeCreationTime = (ulong)DateTime.UtcNow.ToFileTimeUtc(),
            VolumeSerialNumber = (uint)collection.Id.GetHashCode(),
        };
        int result;
        try
        {
            result = letter is null ? PrefixOnlyMount.Mount(host) : host.Mount(letter + ":", null, false, 0);
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or EntryPointNotFoundException or TargetInvocationException)
        {
            // the layer's static initialiser failed (no driver, a driver it refuses, no file path for its version check);
            // reflection in PrefixOnlyMount wraps that in a TargetInvocationException
            host.Dispose();
            throw new InvalidOperationException(WinFspRuntime.Explain(ex), ex);
        }
        catch
        {
            host.Dispose();
            throw;
        }

        if (result < 0)
        {
            host.Dispose();
            throw new InvalidOperationException($"WinFsp refused the mount: NTSTATUS 0x{result:X8}" + (result == unchecked((int)0xC0000035) ? $" (a volume with the prefix {prefix} already exists; is another anyfs running?)" : ""));
        }

        _host = host;
        var unc = $@"\\anyfs\{collection.Name}";
        var paths = new List<AccessPath> { new("UNC path", unc) };
        if (letter is not null)
        {
            paths.Add(new AccessPath("Drive", DriveLetter.Root(letter)));
        }

        AccessPaths = paths;
        // the letter opens in a blink; the UNC path makes Windows look the name up on the network first (docs/mounts.md)
        OpenAction = new MountOpenAction.FileManager(letter is null ? unc : DriveLetter.Root(letter));
    }

    public Task StopAsync(CancellationToken ct)
    {
        var host = _host;
        _host = null;
        if (host is not null)
        {
            return Task.Run(() =>
            {
                host.Unmount();
                host.Dispose();
                AccessPaths = [];
                OpenAction = null;
            }, CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => new(StopAsync(CancellationToken.None));
}

/// <summary>Where the WinFsp runtime is, which version this build needs, and the tool that installs it.</summary>
internal static class WinFspRuntime
{
    /// <summary>
    /// The driver this build's WinFsp .NET layer accepts: the same major and at least this minor (WinFsp's own rule in
    /// Fsp.Interop.Api.CheckVersion), so a newer 2.x driver is fine and an older one fails to load with "incorrect dll
    /// version". <see cref="WinFspInstaller.Version"/> follows the winfsp.net package (a test holds them together) and
    /// AnyFS.App checks the bundled installer against the package at build.
    /// </summary>
    public static readonly Version Required = Version.Parse(WinFspInstaller.Version);

    public static bool Accepts(Version installed, Version required) => installed.Major == required.Major && installed.Minor >= required.Minor;

    [SupportedOSPlatform("windows")]
    public static bool IsInstalled() => FindDll() is not null;

    /// <summary>The installed driver's version from its DLL (major.minor.build), or null when it isn't there.</summary>
    [SupportedOSPlatform("windows")]
    public static Version? InstalledVersion()
    {
        var dll = FindDll();
        if (dll is null)
        {
            return null;
        }

        try
        {
            var info = FileVersionInfo.GetVersionInfo(dll);
            return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Version(0, 0, 0);
        }
    }

    /// <summary>Null when the driver is there and new enough; otherwise why the mount can't start, as the tile and the Add mount dialog show it.</summary>
    [SupportedOSPlatform("windows")]
    public static string? Problem()
    {
        var installed = InstalledVersion();
        if (installed is null)
        {
            return "The WinFsp driver is not installed.";
        }

        return Accepts(installed, Required) ? null : $"WinFsp {installed} is installed; this build needs {Required.Major}.{Required.Minor} or a later {Required.Major}.x.";
    }

    /// <summary>A failure to load WinFsp's .NET layer, in words, for the mount's status.</summary>
    public static string Explain(Exception ex)
    {
        var root = ex.GetBaseException();
        var message = root.Message;
        if (root is DllNotFoundException)
        {
            return "WinFsp is not installed (its DLL could not be loaded): " + message;
        }

        if (root is TypeLoadException && message.Contains("incorrect dll version", StringComparison.OrdinalIgnoreCase))
        {
            return $"The installed WinFsp driver is a version this build does not accept ({message}); upgrade it to WinFsp {Required.Major}.{Required.Minor} or a later {Required.Major}.x.";
        }

        if (root is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            return $"WinFsp's .NET layer ({WinFspInterop.AssemblyName}) could not be loaded: {message}";
        }

        if (root is ArgumentException && ex is not ArgumentException)
        {
            return $"WinFsp's .NET layer could not read its own file version (a host that runs it from memory instead of a file): {message}";
        }

        return $"WinFsp's .NET layer could not start ({root.GetType().Name}: {message})";
    }

    /// <summary>The driver DLL for this process's architecture: where the installer registered it, or the default folder.</summary>
    [SupportedOSPlatform("windows")]
    private static string? FindDll()
    {
        var name = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "winfsp-a64.dll",
            Architecture.X86 => "winfsp-x86.dll",
            _ => "winfsp-x64.dll",
        };
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\WinFsp") ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WinFsp");
            if (key?.GetValue("InstallDir") is string dir && File.Exists(Path.Combine(dir, "bin", name)))
            {
                return Path.Combine(dir, "bin", name);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }

        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "WinFsp", "bin", name);
        return File.Exists(fallback) ? fallback : null;
    }

    public static string? FindWinget()
    {
        var apps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
        if (File.Exists(apps))
        {
            return apps;
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir.Trim(), "winget.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}

/// <summary>
/// winfsp.net's Mount always sets a mount point (null means a drive letter), but the C API doesn't need one for a
/// network file system: spike 1 showed \\anyfs\&lt;collection&gt; working with none. This replicates
/// FileSystemHost.MountEx minus FspFileSystemSetMountPointEx through reflection on the internal interop class,
/// pinned to winfsp.net 2.2.26286 (the same internals as 2.1.25156; the App roots the assembly whole for trimming). The
/// interim until winfsp.net grows a prefix-only overload.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class PrefixOnlyMount
{
    private const BindingFlags Instance = BindingFlags.NonPublic | BindingFlags.Instance;
    private const BindingFlags Static = BindingFlags.NonPublic | BindingFlags.Static;
    private const string Rooted = "winfsp-msil is a TrimmerRootAssembly of the App (spec 14.1): nothing in it is trimmed, so these lookups hold.";

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = Rooted)]
    [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = Rooted)]
    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = Rooted)]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = Rooted)]
    public static int Mount(FileSystemHost host)
    {
        var hostType = typeof(FileSystemHost);
        var api = hostType.Assembly.GetType("Fsp.Interop.Api", throwOnError: false) ?? throw Changed("Fsp.Interop.Api");
        var fileSystem = host.FileSystem();
        var volumeParams = hostType.GetField("_VolumeParams", Instance) ?? throw Changed("_VolumeParams");
        var interfacePtr = hostType.GetField("_FileSystemInterfacePtr", Static) ?? throw Changed("_FileSystemInterfacePtr");
        var fileSystemPtr = hostType.GetField("_FileSystemPtr", Instance) ?? throw Changed("_FileSystemPtr");
        var create = Delegate(api, "FspFileSystemCreate");
        var guard = Delegate(api, "FspFileSystemSetOperationGuardStrategy");
        var debug = Delegate(api, "FspFileSystemSetDebugLog");
        var dispatcher = Delegate(api, "FspFileSystemStartDispatcher");
        var setUserContext = api.GetMethod("SetUserContext", Static) ?? throw Changed("SetUserContext");

        var result = fileSystem.Init(host);
        if (result < 0)
        {
            return result;
        }

        var createArgs = new object?[] { "WinFsp.Net", volumeParams.GetValue(host), interfacePtr.GetValue(null), IntPtr.Zero };
        result = (int)create.DynamicInvoke(createArgs)!;
        if (result < 0)
        {
            return result;
        }

        var pointer = (IntPtr)createArgs[3]!;
        volumeParams.SetValue(host, createArgs[1]);
        setUserContext.Invoke(null, [pointer, fileSystem]);
        guard.DynamicInvoke(pointer, 0);
        debug.DynamicInvoke(pointer, 0u);
        result = fileSystem.Mounted(host);
        if (result < 0)
        {
            return result;
        }

        result = (int)dispatcher.DynamicInvoke(pointer, 0u)!;
        if (result < 0)
        {
            return result;
        }

        fileSystemPtr.SetValue(host, pointer);
        return 0;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = Rooted)]
    private static Delegate Delegate(Type api, string name)
        => (api.GetField(name, Static)?.GetValue(null) as Delegate) ?? throw Changed(name);

    private static InvalidOperationException Changed(string member)
        => new($"winfsp.net's internals changed ({member} not found); the prefix-only mount needs winfsp.net 2.2.26286");
}
