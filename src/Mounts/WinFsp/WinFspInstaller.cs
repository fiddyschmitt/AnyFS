using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using AnyFS.Sdk;

namespace AnyFS.Mounts.WinFsp;

/// <summary>
/// The WinFsp installer a Windows build carries (spec 8.1, N6): src/AnyFS.App embeds tools/winfsp/winfsp-*.msi as
/// "winfsp.msi" (scripts/fetch-winfsp.ps1 fetches the pinned release and checks its published hash), and the Install
/// button runs it through msiexec with no installer windows, so the only thing the user sees is the administrator
/// prompt. The default feature set (Core: driver, launcher and DLL) is what anyfs needs. WinFsp is GPLv3 with a FLOSS
/// exception; the MSI is conveyed unmodified and its source is at https://github.com/winfsp/winfsp (tag v2.2B5).
/// </summary>
internal static class WinFspInstaller
{
    public const string ResourceName = "winfsp.msi";

    /// <summary>
    /// The release scripts/winfsp.json pins, shown in the button's tooltip: WinFsp 2026 Beta 5, whose network provider
    /// accepts a connection to a share no launcher manages, so Explorer opens \\anyfs\&lt;collection&gt; itself and
    /// programs that look a path up through the shell (Paint) open files below it; 2.1's provider refused it.
    /// </summary>
    public const string Version = "2.2.26286";

    private const int ErrorCancelled = 1223;
    private const int SuccessRebootRequired = 3010;

    public static bool HasEmbedded()
    {
        using var stream = OpenEmbedded();
        return stream is not null;
    }

    [SupportedOSPlatform("windows")]
    public static async Task<InstallResult> InstallAsync(IProgress<string> progress, CancellationToken ct)
    {
        progress.Report("Unpacking the bundled WinFsp installer...");
        var msi = Extract();
        if (msi is null)
        {
            return new InstallResult(false, "This build has no WinFsp installer inside it.");
        }

        var log = Path.Combine(Path.GetDirectoryName(msi)!, "winfsp-install.log");
        progress.Report($"Installing WinFsp {Version} silently; approve the administrator prompt when it appears.");
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe"))
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var argument in Arguments(msi, log))
        {
            info.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(info) ?? throw new InvalidOperationException("msiexec did not start");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return new InstallResult(false, "The administrator prompt was declined, so WinFsp was not installed.");
        }

        using (process)
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            var code = process.ExitCode;
            var installed = WinFspRuntime.IsInstalled();
            if (code is 0 or SuccessRebootRequired && installed)
            {
                return new InstallResult(true, code == SuccessRebootRequired
                    ? "WinFsp is installed. Windows asks for a restart to finish, though the driver normally works at once."
                    : "WinFsp is installed. The mount can start now.");
            }

            return new InstallResult(false, $"{Describe(code, installed)} The installer log is {log}.");
        }
    }

    /// <summary>msiexec's arguments: install the package with no user interface and no restart, logging verbosely to <paramref name="log"/>.</summary>
    public static IReadOnlyList<string> Arguments(string msi, string log) => ["/i", msi, "/qn", "/norestart", "/l*v", log];

    /// <summary>What an msiexec exit code means, for the message when the install didn't take.</summary>
    public static string Describe(int exitCode, bool installed) => exitCode switch
    {
        0 or SuccessRebootRequired when !installed => "msiexec reported success but the WinFsp driver is still not found.",
        1602 => "The installation was cancelled.",
        1603 => "The installer failed (msiexec exit code 1603).",
        1618 => "Another installation is in progress; try again when it has finished.",
        1619 or 1620 => "The installer package could not be opened.",
        1625 => "A system policy blocks this installation.",
        1638 => "A different WinFsp is already installed and the installer will not replace it; uninstall that one first.",
        _ => $"msiexec exited with code {exitCode}.",
    };

    /// <summary>The MSI written under the user's temp folder, named by its hash so a new build never runs an old copy.</summary>
    private static string? Extract()
    {
        using var resource = OpenEmbedded();
        if (resource is null)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        resource.CopyTo(buffer);
        var bytes = buffer.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes))[..12].ToLowerInvariant();
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "anyfs")).FullName;
        var path = Path.Combine(dir, $"winfsp-{hash}.msi");
        if (!File.Exists(path) || new FileInfo(path).Length != bytes.Length)
        {
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }

        return path;
    }

    /// <summary>The embedded copy: the host executable carries it (it alone knows its RID), this assembly may too.</summary>
    private static Stream? OpenEmbedded()
    {
        foreach (var assembly in new[] { Assembly.GetEntryAssembly(), typeof(WinFspInstaller).Assembly })
        {
            if (assembly?.GetManifestResourceStream(ResourceName) is { } stream)
            {
                return stream;
            }
        }

        return null;
    }
}
