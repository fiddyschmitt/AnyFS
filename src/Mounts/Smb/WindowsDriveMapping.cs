using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace AnyFS.Mounts.Smb;

/// <summary>
/// The opt-in drive letter of an SMB mount on Windows: <c>net use X: \\host\collection /TCPPORT:port</c> once the
/// server is up (Windows 11 24H2+ takes the port that way), <c>net use X: /delete</c> when the mount stops. A letter
/// left pointing at our share by a crash is taken back; one in use by anything else is refused, and the mount carries
/// on with the UNC path and a yellow note.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsDriveMapping
{
    private const int ErrorConnectionUnavail = 1201;
    private const int ErrorMoreData = 234;
    private static readonly TimeSpan NetUseTimeout = TimeSpan.FromSeconds(60);

    public sealed record Outcome(bool Success, string Message);

    /// <summary>
    /// Maps <paramref name="letter"/> with the given <c>net use</c> arguments (<see cref="SmbMountProvider.WindowsConnectArguments"/>
    /// with the letter). A <paramref name="guest"/> share cannot be mapped where Windows blocks insecure guest logons (the
    /// default on Pro and Enterprise), and net use takes twenty seconds to say so, hence the check up front.
    /// </summary>
    public static async Task<Outcome> MapAsync(string letter, string unc, IReadOnlyList<string> netUseArguments, bool guest, ILogger logger, CancellationToken ct)
    {
        var device = letter + ":";
        if (guest && !GuestLogonsAllowed())
        {
            return new Outcome(false, $"{device} was not mapped: Windows blocks guest logons to shares (its AllowInsecureGuestAuth policy), so a drive letter needs a user name and password.");
        }

        var current = CurrentTarget(device);
        if (current is not null)
        {
            if (!SameShare(current, unc))
            {
                return new Outcome(false, $"{device} is already mapped to {current}; choose another letter or disconnect it.");
            }

            logger.LogInformation("{Device} still points at {Unc} from an earlier run; mapping it afresh", device, unc);
            await RunNetAsync(["use", device, "/delete", "/y"], ct).ConfigureAwait(false);
        }
        else if (DriveInfo.GetDrives().Any(d => d.Name.StartsWith(device, StringComparison.OrdinalIgnoreCase)))
        {
            return new Outcome(false, $"{device} is in use by a local drive; choose another letter.");
        }

        var (code, output) = await RunNetAsync(netUseArguments, ct).ConfigureAwait(false);
        if (code == 0)
        {
            logger.LogInformation("mapped {Device} to {Unc}", device, unc);
            return new Outcome(true, "");
        }

        return new Outcome(false, $"Mapping {device} failed: {Gist(output, code)} The UNC path still works.");
    }

    public static async Task UnmapAsync(string letter, ILogger logger)
    {
        var device = letter + ":";
        try
        {
            var (code, output) = await RunNetAsync(["use", device, "/delete", "/y"], CancellationToken.None).ConfigureAwait(false);
            if (code == 0)
            {
                logger.LogInformation("removed {Device}", device);
            }
            else
            {
                logger.LogWarning("net use {Device} /delete: {Gist}", device, Gist(output, code));
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.LogWarning(ex, "could not remove {Device}", device);
        }
    }

    /// <summary>The remote name a device is connected (or remembered as connected) to; null when it is free or a local disk.</summary>
    public static unsafe string? CurrentTarget(string device)
    {
        var length = 1024;
        var buffer = new char[length];
        int rc;
        fixed (char* p = buffer)
        {
            rc = WNetGetConnectionW(device, p, ref length);
        }

        if (rc == ErrorMoreData && length > buffer.Length)
        {
            buffer = new char[length];
            fixed (char* p = buffer)
            {
                rc = WNetGetConnectionW(device, p, ref length);
            }
        }

        if (rc is 0 or ErrorConnectionUnavail)
        {
            var name = new string(buffer).TrimEnd('\0');
            return name.Length > 0 ? name : null;
        }

        return null;
    }

    /// <summary>Windows' AllowInsecureGuestAuth: the policy key decides when present, else the workstation service's own value; 1 lets a guest logon through.</summary>
    public static bool GuestLogonsAllowed()
    {
        foreach (var path in new[] { @"SOFTWARE\Policies\Microsoft\Windows\LanmanWorkstation", @"SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters" })
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(path);
                if (key?.GetValue("AllowInsecureGuestAuth") is int value)
                {
                    return value == 1;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
            }
        }

        return false;
    }

    private static bool SameShare(string a, string b) => string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Runs net.exe with stdin closed (a prompt reads end-of-file instead of hanging) and a timeout; (-1, why) when it overran.</summary>
    private static async Task<(int Code, string Output)> RunNetAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "net.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException("net.exe did not start");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(NetUseTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill();
            }
            catch (InvalidOperationException)
            {
            }

            ct.ThrowIfCancellationRequested();
            return (-1, $"net use did not finish within {NetUseTimeout.TotalSeconds:N0} s.");
        }

        var output = (await stdout.ConfigureAwait(false) + "\n" + await stderr.ConfigureAwait(false)).Trim();
        return (process.ExitCode, output);
    }

    /// <summary>net.exe explains itself on its last line ("The local device name is already in use."); the first only names the error number.</summary>
    private static string Gist(string output, int code)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length > 0 ? lines[^1] : $"exit code {code}.";
    }

    [LibraryImport("mpr.dll", EntryPoint = "WNetGetConnectionW", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int WNetGetConnectionW(string localName, char* remoteName, ref int length);
}
