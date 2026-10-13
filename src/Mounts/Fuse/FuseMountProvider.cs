using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AnyFS.Sdk;
using FuseDotNet;
using Microsoft.Extensions.Logging;

namespace AnyFS.Mounts.Fuse;

public sealed class FuseMountSettings
{
    [Setting("Mount point", Required = true, Order = 0, Hint = "An empty folder, created if missing; ~ is your home.")]
    public string MountPoint { get; set; } = "";

    [Setting("Visible to other users", Order = 1, Hint = "allow_other: needs 'user_allow_other' in /etc/fuse.conf. Without it the mount is yours alone.")]
    public bool AllowOther { get; set; } = true;
}

/// <summary>
/// FUSE (type <c>fuse</c>, Linux only, spec 8.3) through LTRData.FuseDotNet over libfuse3: a folder under the user's
/// home (<c>~/anyfs/&lt;collection&gt;</c> by default), unmounted on stop, a stale mount from a crash cleaned up on the
/// next start. Distros ship only the versioned soname, so the P/Invoke name is resolved to libfuse3.so.3 or .so.4.
/// </summary>
public sealed class FuseMountProvider : IMountProviderPlugin, IValidatable
{
    public string TypeId => "fuse";

    public string DisplayName => "FUSE";

    public string Description => "A native file system on this Linux machine through libfuse3: a folder under your home, with the best performance.";

    public Stream? OpenIcon() => null;

    public IReadOnlyList<OSPlatform> SupportedPlatforms { get; } = [OSPlatform.Linux];

    public string? UnavailableReason => !OperatingSystem.IsLinux() ? "FUSE needs Linux (macOS uses SMB, spec 8.3)"
        : FuseRuntime.LibraryAvailable() ? null
        : "libfuse3 was not found. Install the fuse3 package (apt install fuse3, dnf install fuse3).";

    public PerformanceTier Tier => PerformanceTier.Native;

    public bool SupportsWrites => true;

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicConstructors)]
    public Type SettingsType => typeof(FuseMountSettings);

    public object CreateDefaultSettings(ICollectionInfo collection) => new FuseMountSettings { MountPoint = $"~/anyfs/{collection.Name}" };

    public IMount CreateMount(object settings, ICollectionInfo collection) => new FuseMount((FuseMountSettings)settings, collection);

    public IEnumerable<ValidationMessage> Validate(object settings)
    {
        var s = (FuseMountSettings)settings;
        var text = s.MountPoint?.Trim() ?? "";
        if (text.Length == 0)
        {
            yield return new ValidationMessage("Give the mount a folder, e.g. ~/anyfs/work.", nameof(FuseMountSettings.MountPoint));
        }
        else if (!text.StartsWith('/') && !text.StartsWith("~/", StringComparison.Ordinal) && text != "~")
        {
            yield return new ValidationMessage("Use an absolute path (/mnt/work) or one under your home (~/anyfs/work).", nameof(FuseMountSettings.MountPoint));
        }
    }

    public static string ExpandMountPoint(string text)
    {
        text = text.Trim();
        if (text == "~" || text.StartsWith("~/", StringComparison.Ordinal))
        {
            var home = Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            text = home.TrimEnd('/') + text[1..];
        }

        return text.Length > 1 ? text.TrimEnd('/') : text;
    }
}

internal sealed class FuseMount(FuseMountSettings settings, ICollectionInfo collection) : IMount
{
    private readonly ManualResetEventSlim _exited = new();
    private Thread? _thread;
    private string? _mountPoint;
    private Exception? _mountError;

    public IReadOnlyList<AccessPath> AccessPaths { get; private set; } = [];

    public MountOpenAction? OpenAction { get; private set; }

    public async Task StartAsync(IVirtualFileSystem fs, IMountContext context, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new InvalidOperationException("FUSE needs Linux");
        }

        await StartOnLinuxAsync(fs, context, ct).ConfigureAwait(false);
    }

    [SupportedOSPlatform("linux")]
    private async Task StartOnLinuxAsync(IVirtualFileSystem fs, IMountContext context, CancellationToken ct)
    {
        FuseRuntime.EnsureResolver();
        var path = FuseMountProvider.ExpandMountPoint(settings.MountPoint);
        if (path.Length == 0 || !path.StartsWith('/'))
        {
            throw new InvalidOperationException($"'{settings.MountPoint}' is not a usable mount point");
        }

        if (FuseRuntime.IsMounted(path))
        {
            context.Logger.LogWarning("fuse: {Path} is still mounted from an earlier run; unmounting it first", path);
            FuseRuntime.Unmount(path, context.Logger);
        }

        Directory.CreateDirectory(path);
        if (Directory.EnumerateFileSystemEntries(path).Any())
        {
            throw new InvalidOperationException($"the mount point {path} is not empty");
        }

        var allowOther = settings.AllowOther && FuseRuntime.UserAllowOther();
        var options = $"fsname=anyfs,subtype=anyfs,uid={FuseRuntime.Uid()},gid={FuseRuntime.Gid()},default_permissions" + (allowOther ? ",allow_other" : "");
        var operations = new FuseFileSystem(fs, FuseRuntime.Uid(), FuseRuntime.Gid(), context.Logger);
        string[] args = ["anyfs", "-f", "-o", options, path];
        _exited.Reset();
        _mountError = null;
        var thread = new Thread(() =>
        {
            try
            {
                operations.Mount(args);
            }
            catch (Exception ex)
            {
                _mountError = ex;
            }
            finally
            {
                _exited.Set();
            }
        })
        {
            IsBackground = true,
            Name = "fuse " + collection.Name,
        };
        _thread = thread;
        _mountPoint = path;
        thread.Start();

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!FuseRuntime.IsMounted(path))
        {
            if (_exited.IsSet)
            {
                _thread = null;
                _mountPoint = null;
                throw new InvalidOperationException("FUSE did not mount: " + (_mountError?.GetBaseException().Message ?? "libfuse returned before mounting (is the fuse3 package installed, and is /dev/fuse accessible?)"));
            }

            if (DateTime.UtcNow > deadline)
            {
                FuseRuntime.Unmount(path, context.Logger);
                throw new InvalidOperationException($"FUSE did not mount {path} within 10 s");
            }

            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        AccessPaths = [new AccessPath("Mount point", path)];
        OpenAction = new MountOpenAction.FileManager(path);
        if (settings.AllowOther && !allowOther)
        {
            context.ReportHealth(ComponentHealth.Degraded, "Only you can see this mount: 'user_allow_other' is not set in /etc/fuse.conf, so allow_other was left off.");
        }
    }

    public Task StopAsync(CancellationToken ct)
    {
        var path = _mountPoint;
        var thread = _thread;
        _mountPoint = null;
        _thread = null;
        if (path is null)
        {
            return Task.CompletedTask;
        }

        return Task.Run(() =>
        {
            if (!OperatingSystem.IsLinux())
            {
                return;
            }

            FuseRuntime.Unmount(path, null);
            if (thread is not null && !_exited.Wait(TimeSpan.FromSeconds(5)))
            {
                FuseRuntime.Fusermount(path, null);
                _exited.Wait(TimeSpan.FromSeconds(5));
            }

            AccessPaths = [];
            OpenAction = null;
        }, CancellationToken.None);
    }

    public ValueTask DisposeAsync() => new(StopAsync(CancellationToken.None));
}

/// <summary>What the FUSE provider needs from the OS: the library, the mount table, the user's ids, the helper.</summary>
[SupportedOSPlatform("linux")]
internal static partial class FuseRuntime
{
    private static readonly string[] Candidates = ["libfuse3.so.3", "libfuse3.so.4", "libfuse3.so"];
    private static int _resolverSet;

    /// <summary>FuseDotNet P/Invokes "fuse3"; distros without the -dev package ship only the versioned soname (spike 3).</summary>
    public static void EnsureResolver()
    {
        if (Interlocked.Exchange(ref _resolverSet, 1) != 0)
        {
            return;
        }

        NativeLibrary.SetDllImportResolver(typeof(FuseDotNet.Fuse).Assembly, (name, _, _) =>
        {
            if (name != "fuse3")
            {
                return IntPtr.Zero;
            }

            foreach (var candidate in Candidates)
            {
                if (NativeLibrary.TryLoad(candidate, out var handle))
                {
                    return handle;
                }
            }

            return IntPtr.Zero;
        });
    }

    public static bool LibraryAvailable()
    {
        foreach (var candidate in Candidates)
        {
            if (NativeLibrary.TryLoad(candidate, out var handle))
            {
                NativeLibrary.Free(handle);
                return true;
            }
        }

        return false;
    }

    /// <summary>/etc/fuse.conf must contain an uncommented user_allow_other for allow_other to be accepted.</summary>
    public static bool UserAllowOther()
    {
        try
        {
            return File.Exists("/etc/fuse.conf") && File.ReadLines("/etc/fuse.conf").Any(line => line.Trim() == "user_allow_other");
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool IsMounted(string path)
    {
        try
        {
            var escaped = path.Replace("\\", "\\134").Replace(" ", "\\040").Replace("\t", "\\011").Replace("\n", "\\012");
            return File.ReadLines("/proc/mounts").Any(line =>
            {
                var fields = line.Split(' ');
                return fields.Length > 1 && fields[1] == escaped;
            });
        }
        catch (IOException)
        {
            return false;
        }
    }

    public static void Unmount(string path, ILogger? logger)
    {
        if (!FuseDotNet.Fuse.TryUnmount(path, out var result))
        {
            logger?.LogDebug("fuse: unmount of {Path} returned {Result}; trying fusermount3", path, result);
            Fusermount(path, logger);
        }
    }

    public static void Fusermount(string path, ILogger? logger)
    {
        try
        {
            var info = new ProcessStartInfo("fusermount3") { UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add("-u");
            info.ArgumentList.Add(path);
            using var process = Process.Start(info);
            process?.WaitForExit(5000);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger?.LogDebug(ex, "fuse: fusermount3 -u {Path} failed", path);
        }
    }

    public static uint Uid() => getuid();

    public static uint Gid() => getgid();

    [LibraryImport("libc")]
    private static partial uint getuid();

    [LibraryImport("libc")]
    private static partial uint getgid();
}
