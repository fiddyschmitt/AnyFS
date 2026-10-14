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
/// drive letter (a letter is opt-in). The WinFsp runtime is a driver installed separately; the provider notices its
/// absence and offers to install it with winget (N6).
/// </summary>
public sealed class WinFspMountProvider : IMountProviderPlugin, IValidatable, IInstallableProvider
{
    public const string WingetId = "WinFsp.WinFsp";

    public string TypeId => "winfsp";

    public string DisplayName => "WinFsp";

    public string Description => "A native file system for this machine: \\\\anyfs\\<collection> in Explorer and every program, with the best performance. Needs the WinFsp driver.";

    public Stream? OpenIcon() => null;

    public IReadOnlyList<OSPlatform> SupportedPlatforms { get; } = [OSPlatform.Windows];

    public string? UnavailableReason => !OperatingSystem.IsWindows() ? "WinFsp is a Windows driver"
        : WinFspRuntime.IsInstalled() ? null
        : "WinFsp is not installed. Install it (the button runs winget install WinFsp.WinFsp) or get it from https://winfsp.dev; no reboot is needed.";

    public PerformanceTier Tier => PerformanceTier.Native;

    public bool SupportsWrites => true;

    /// <summary>The driver install is the one step that needs an administrator; the mount itself doesn't.</summary>
    public bool NeedsAdministrator => OperatingSystem.IsWindows() && !WinFspRuntime.IsInstalled();

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors)]
    public Type SettingsType => typeof(WinFspMountSettings);

    public object CreateDefaultSettings(ICollectionInfo collection) => new WinFspMountSettings();

    public IMount CreateMount(object settings, ICollectionInfo collection) => new WinFspMount((WinFspMountSettings)settings, collection);

    public IEnumerable<ValidationMessage> Validate(object settings)
    {
        var s = (WinFspMountSettings)settings;
        if (!string.IsNullOrWhiteSpace(s.DriveLetter) && NormaliseLetter(s.DriveLetter) is null)
        {
            yield return new ValidationMessage("A drive letter is a single letter, D to Z.", nameof(WinFspMountSettings.DriveLetter));
        }
    }

    public string InstallText => "Install WinFsp";

    public string InstallDetail => $"Runs: winget install --id {WingetId} (an administrator prompt appears for the installer; no reboot)";

    public bool CanInstall => OperatingSystem.IsWindows() && WinFspRuntime.FindWinget() is not null;

    public async Task<InstallResult> InstallAsync(IProgress<string> progress, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new InstallResult(false, "WinFsp is a Windows driver.");
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
        var installed = WinFspRuntime.IsInstalled();
        return installed
            ? new InstallResult(true, "WinFsp is installed. The mount can start now.")
            : new InstallResult(false, $"winget exited with code {process.ExitCode} and WinFsp is still missing: {string.Join(" | ", tail)}");

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

    public static string? NormaliseLetter(string? text)
    {
        var letter = text?.Trim().TrimEnd(':', '\\', '/').ToUpperInvariant();
        return letter is { Length: 1 } && letter[0] is >= 'D' and <= 'Z' ? letter : null;
    }
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

            Mount(fs, context);
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
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException or EntryPointNotFoundException)
        {
            host.Dispose();
            throw new InvalidOperationException("WinFsp is not installed (its DLL could not be loaded): " + ex.GetBaseException().Message, ex);
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
            paths.Add(new AccessPath("Drive", letter + @":\"));
        }

        AccessPaths = paths;
        OpenAction = new MountOpenAction.FileManager(unc);
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

/// <summary>Where the WinFsp runtime is, and the tool that installs it.</summary>
internal static class WinFspRuntime
{
    [SupportedOSPlatform("windows")]
    public static bool IsInstalled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\WinFsp") ?? Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WinFsp");
            if (key?.GetValue("InstallDir") is string dir && File.Exists(Path.Combine(dir, "bin", "winfsp-x64.dll")))
            {
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        return File.Exists(Path.Combine(programFiles, "WinFsp", "bin", "winfsp-x64.dll"));
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
/// pinned to winfsp.net 2.1.25156 (the App roots the assembly whole for trimming). The interim until winfsp.net
/// grows a prefix-only overload.
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
        => new($"winfsp.net's internals changed ({member} not found); the prefix-only mount needs winfsp.net 2.1.25156");
}
