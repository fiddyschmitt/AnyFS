using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
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
/// Finds the portable-smb-server executable: ANYFS_SMB_SERVER, a copy beside anyfs, or the one embedded at publish
/// (extracted into the state folder on first use, named by its hash so a new build never runs an old copy).
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

        reason = $"The SMB server (portable-smb-server) is not bundled in this build. Put {FileName} beside anyfs, or set {EnvironmentVariable} to its path.";
        return false;
    }

    /// <summary>The path to run, extracting the embedded copy into <paramref name="stateDirectory"/> when that is what we have.</summary>
    public static string Locate(string stateDirectory)
    {
        if (FromEnvironment() is { } env)
        {
            return env;
        }

        if (BesideExecutable() is { } beside)
        {
            return beside;
        }

        using var resource = typeof(SmbServerBinary).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"the SMB server is not bundled in this build; put {FileName} beside anyfs or set {EnvironmentVariable}");
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

    private static bool HasEmbedded() => typeof(SmbServerBinary).Assembly.GetManifestResourceNames().Contains(ResourceName, StringComparer.Ordinal);
}
