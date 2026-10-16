using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging;

namespace AnyFS.Mounts.Smb;

/// <summary>The login every share on one server uses (the server has one user, or guest).</summary>
internal sealed record SmbCredentials(string? User, string? Password, bool Guest)
{
    public bool SameAs(SmbCredentials other) => Guest == other.Guest && (Guest || (string.Equals(User, other.User, StringComparison.Ordinal) && string.Equals(Password, other.Password, StringComparison.Ordinal)));

    public string Describe() => Guest ? "guest access" : $"user '{User}'";
}

/// <summary>
/// One portable-smb-server process for every collection mounted on the same bind address and port (spec 8.2),
/// restarted whenever the set of shares changes (clients reconnect; SMB tolerates it), fed by one <see cref="VfsEndpoint"/>.
/// </summary>
internal sealed class SmbServerProcess : IAsyncDisposable
{
    private readonly IPAddress _address;
    private readonly int _port;
    private readonly string _binary;
    private readonly ILogger _logger;
    private readonly VfsEndpoint _endpoint;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Registration> _shares = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _recentOutput = new();
    private Process? _process;
    private SmbCredentials? _credentials;
    private bool _stopping;

    private SmbServerProcess(IPAddress address, int port, string binary, VfsEndpoint endpoint, ILogger logger)
    {
        _address = address;
        _port = port;
        _binary = binary;
        _endpoint = endpoint;
        _logger = logger;
    }

    /// <summary>Raised when the server dies on its own (the message explains), so the mounts can report it.</summary>
    public event Action<string>? Died;

    public int ShareCount => _shares.Count;

    public bool IsRunning => _process is { HasExited: false };

    public static async Task<SmbServerProcess> StartAsync(IPAddress address, int port, string binary, ILogger logger, CancellationToken ct)
    {
        var endpoint = await VfsEndpoint.StartAsync(logger, ct).ConfigureAwait(false);
        return new SmbServerProcess(address, port, binary, endpoint, logger);
    }

    public async Task RegisterAsync(string collection, IVirtualFileSystem fs, IMountStatsSink stats, SmbCredentials credentials, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_credentials is not null && !_credentials.SameAs(credentials))
            {
                throw new InvalidOperationException($"the SMB server on {_address}:{_port} already runs with {_credentials.Describe()}; use the same login for every collection on this port, or another port");
            }

            if (_shares.ContainsKey(collection))
            {
                throw new InvalidOperationException($"'{collection}' is already served on port {_port} by another mount");
            }

            _endpoint.Register(collection, fs, stats);
            _shares[collection] = new Registration(collection);
            _credentials ??= credentials;
            try
            {
                await RestartAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                _shares.Remove(collection);
                _endpoint.Unregister(collection);
                if (_shares.Count == 0)
                {
                    _credentials = null;
                }

                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UnregisterAsync(string collection)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_shares.Remove(collection))
            {
                return;
            }

            _endpoint.Unregister(collection);
            if (_shares.Count == 0)
            {
                _credentials = null;
                StopProcess();
            }
            else
            {
                try
                {
                    await RestartAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "SMB server on {Address}:{Port}: restart after removing '{Collection}' failed", _address, _port, collection);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            StopProcess();
        }
        finally
        {
            _gate.Release();
        }

        await _endpoint.DisposeAsync().ConfigureAwait(false);
        _logger.LogInformation("SMB server on {Address}:{Port} stopped", _address, _port);
    }

    private async Task RestartAsync(CancellationToken ct)
    {
        StopProcess();
        var info = new ProcessStartInfo(_binary)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in Arguments())
        {
            info.ArgumentList.Add(argument);
        }

        lock (_recentOutput)
        {
            _recentOutput.Clear();
        }

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Remember(e.Data);
        process.ErrorDataReceived += (_, e) => Remember(e.Data);
        process.Exited += (_, _) => OnExited(process);
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            throw new InvalidOperationException($"could not start the SMB server '{_binary}': {ex.Message}", ex);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        ChildJob.Attach(process, _logger);
        _stopping = false;
        _process = process;

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                _process = null;
                throw new InvalidOperationException($"the SMB server exited with code {process.ExitCode}: {RecentOutput()}");
            }

            if (await ProbeAsync(ct).ConfigureAwait(false))
            {
                _logger.LogInformation("SMB server listening on {Address}:{Port} with {Count} share(s)", _address, _port, _shares.Count);
                return;
            }

            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        StopProcess();
        throw new InvalidOperationException($"the SMB server did not start listening on {_address}:{_port} within 15 s: {RecentOutput()}");
    }

    private IEnumerable<string> Arguments()
    {
        yield return "-ip";
        yield return _address.ToString();
        yield return "-port";
        yield return _port.ToString();
        if (_credentials is { Guest: true })
        {
            yield return "-guest";
        }
        else if (_credentials is not null)
        {
            yield return "-user";
            yield return _credentials.User ?? "";
            yield return "-pass";
            yield return _credentials.Password ?? "";
        }

        yield return "-vfs-token";
        yield return _endpoint.Token;
        // the server quits when anyfs dies (a crash, a kill): no orphan holding the port on the next start
        yield return "-exit-with-parent";
        foreach (var share in _shares.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            yield return "-vfs";
            yield return _endpoint.BaseUrlFor(share);
            yield return "-share";
            yield return share;
        }
    }

    private async Task<bool> ProbeAsync(CancellationToken ct)
    {
        var target = _address.Equals(IPAddress.Any) ? IPAddress.Loopback : _address.Equals(IPAddress.IPv6Any) ? IPAddress.IPv6Loopback : _address;
        try
        {
            using var client = new TcpClient(target.AddressFamily);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(500);
            await client.ConnectAsync(target, _port, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private void StopProcess()
    {
        var process = _process;
        _process = null;
        if (process is null)
        {
            return;
        }

        _stopping = true;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _logger.LogDebug(ex, "SMB server: stop");
        }
        finally
        {
            process.Dispose();
        }
    }

    private void OnExited(Process process)
    {
        if (_stopping || !ReferenceEquals(process, _process))
        {
            return;
        }

        var message = $"the SMB server exited with code {process.ExitCode}: {RecentOutput()}";
        _logger.LogError("SMB server on {Address}:{Port}: {Message}", _address, _port, message);
        _process = null;
        Died?.Invoke(message);
    }

    private void Remember(string? line)
    {
        if (line is null)
        {
            return;
        }

        _logger.LogDebug("smb: {Line}", line);
        lock (_recentOutput)
        {
            _recentOutput.Enqueue(line);
            while (_recentOutput.Count > 20)
            {
                _recentOutput.Dequeue();
            }
        }
    }

    private string RecentOutput()
    {
        lock (_recentOutput)
        {
            return _recentOutput.Count == 0 ? "(no output)" : string.Join(" | ", _recentOutput);
        }
    }

    private sealed record Registration(string Name);
}

/// <summary>
/// On Windows, a job object with kill-on-close: when anyfs ends in any way (a crash, Stop-Process), the server
/// process goes with it instead of lingering on the port. Elsewhere the server's own -exit-with-parent does this.
/// </summary>
internal static partial class ChildJob
{
    private const int ExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;
    private static readonly object Gate = new();
    private static IntPtr _job;

    public static void Attach(Process process, ILogger logger)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            lock (Gate)
            {
                if (_job == IntPtr.Zero)
                {
                    _job = CreateJobObjectW(IntPtr.Zero, null);
                    if (_job == IntPtr.Zero)
                    {
                        return;
                    }

                    var info = new JobObjectExtendedLimitInformation { BasicLimitInformation = { LimitFlags = JobObjectLimitKillOnJobClose } };
                    var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
                    var buffer = Marshal.AllocHGlobal(size);
                    try
                    {
                        Marshal.StructureToPtr(info, buffer, false);
                        SetInformationJobObject(_job, ExtendedLimitInformationClass, buffer, (uint)size);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }

                AssignProcessToJobObject(_job, process.Handle);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.LogDebug(ex, "SMB server: could not attach the job object");
        }
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CreateJobObjectW(IntPtr attributes, string? name);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }
}

/// <summary>
/// Finds the portable-smb-server executable: the one embedded in the build (extracted into the state folder on first
/// use, named by its hash so a new build never runs an old copy). For development, ANYFS_SMB_SERVER or a copy beside
/// anyfs take precedence; neither is mentioned to users, since every build carries the server.
/// </summary>
internal static class SmbServerBinary
{
    public const string EnvironmentVariable = "ANYFS_SMB_SERVER";
    public const string ResourceName = "portable-smb-server";

    public static string FileName => OperatingSystem.IsWindows() ? "portable-smb-server.exe" : "portable-smb-server";

    /// <summary>Whether a binary can be found without extracting anything (for <c>UnavailableReason</c>).</summary>
    public static bool IsAvailable(out string reason)
    {
        if (FromEnvironment() is not null || BesideExecutable() is not null)
        {
            reason = "";
            return true;
        }

        if (HasEmbedded())
        {
            reason = "";
            return true;
        }

        reason = "The bundled SMB server is missing from this build: run scripts/fetch-go-servers.ps1 and build again.";
        return false;
    }

    /// <summary>The path to run, extracting the embedded copy into <paramref name="stateDirectory"/> when that is what we have.</summary>
    public static string Locate(string stateDirectory)
    {
        if (FromEnvironment() is { } env)
        {
            return EnsureExecutable(env);
        }

        if (BesideExecutable() is { } beside)
        {
            return EnsureExecutable(beside);
        }

        using var resource = OpenEmbedded()
            ?? throw new InvalidOperationException("the bundled SMB server is missing from this build: run scripts/fetch-go-servers.ps1 and build again");
        using var buffer = new MemoryStream();
        resource.CopyTo(buffer);
        var bytes = buffer.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes))[..12].ToLowerInvariant();
        var dir = Directory.CreateDirectory(Path.Combine(stateDirectory, "bin")).FullName;
        var path = Path.Combine(dir, OperatingSystem.IsWindows() ? $"portable-smb-server-{hash}.exe" : $"portable-smb-server-{hash}");
        if (!File.Exists(path) || new System.IO.FileInfo(path).Length != bytes.Length)
        {
            var temp = path + ".tmp";
            File.WriteAllBytes(temp, bytes);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            File.Move(temp, path, overwrite: true);
        }

        return path;
    }

    /// <summary>
    /// A copy that was downloaded or checked out on Linux/macOS may carry no execute bit (a bare download has no mode);
    /// set it rather than fail to start. A location we cannot change is left alone and the start reports its own error.
    /// </summary>
    private static string EnsureExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                var mode = File.GetUnixFileMode(path);
                if ((mode & UnixFileMode.UserExecute) == 0)
                {
                    File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        return path;
    }

    private static string? FromEnvironment()
    {
        var value = Environment.GetEnvironmentVariable(EnvironmentVariable);
        return !string.IsNullOrWhiteSpace(value) && File.Exists(value) ? value : null;
    }

    private static string? BesideExecutable()
    {
        var path = Path.Combine(AppContext.BaseDirectory, FileName);
        return File.Exists(path) ? path : null;
    }

    private static bool HasEmbedded()
    {
        using var stream = OpenEmbedded();
        return stream is not null;
    }

    /// <summary>The embedded copy: the host executable carries it (it alone knows its RID at publish), this assembly may too.</summary>
    private static Stream? OpenEmbedded()
    {
        foreach (var assembly in new[] { Assembly.GetEntryAssembly(), typeof(SmbServerBinary).Assembly })
        {
            if (assembly?.GetManifestResourceStream(ResourceName) is { } stream)
            {
                return stream;
            }
        }

        return null;
    }
}
