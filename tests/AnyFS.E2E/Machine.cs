using System.Diagnostics;
using System.Text;
using Renci.SshNet;

namespace AnyFS.E2E;

/// <summary>
/// A lab machine over SSH with the lab key: commands with a hard wall-clock bound (SSH.NET's command timeout
/// covers execution only, not the channel open that a tired machine can hang on, as the ft lab found), file
/// upload and download. Windows machines run PowerShell; the others a POSIX shell.
/// </summary>
public sealed class Machine : IDisposable
{
    private readonly ConnectionInfo _connection;
    private readonly SshClient _ssh;

    private Machine(LabMachine machine, ConnectionInfo connection, SshClient ssh)
    {
        Info = machine;
        _connection = connection;
        _ssh = ssh;
    }

    public LabMachine Info { get; }

    public string Name => Info.Name;

    public string Ip => Info.Ip;

    public bool IsWindows => Info.Os == "windows";

    public bool IsMac => Info.Os == "macos";

    public bool IsLinux => Info.Os == "linux";

    /// <summary>Connects, or returns null when the machine can't be reached (its rows are then Inconclusive).</summary>
    public static Machine? TryConnect(LabMachine machine, string keyPath, TimeSpan? timeout = null)
    {
        try
        {
            var key = new PrivateKeyFile(keyPath);
            var connection = new ConnectionInfo(machine.Ip, 22, machine.User, new PrivateKeyAuthenticationMethod(machine.User, key))
            {
                Timeout = timeout ?? TimeSpan.FromSeconds(15),
            };
            var ssh = new SshClient(connection);
            ssh.KeepAliveInterval = TimeSpan.FromSeconds(15);
            ssh.Connect();
            return new Machine(machine, connection, ssh);
        }
        catch (Exception ex) when (ex is Renci.SshNet.Common.SshException or System.Net.Sockets.SocketException or IOException or TimeoutException or InvalidOperationException)
        {
            Trace.WriteLine($"{machine.Name} ({machine.Ip}): {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Runs a command and returns its exit code and output (stdout then stderr); -1 and a note when it overran the bound.</summary>
    public (int Code, string Output) Run(string command, int timeoutSeconds = 120)
    {
        var budget = TimeSpan.FromSeconds(timeoutSeconds);
        var cmd = _ssh.CreateCommand(command);
        cmd.CommandTimeout = budget;
        var task = Task.Run(() =>
        {
            try
            {
                return cmd.Execute();
            }
            catch (Exception ex) when (ex is Renci.SshNet.Common.SshException or IOException or InvalidOperationException or OperationCanceledException)
            {
                return "[ssh: " + ex.Message + "]";
            }
        });
        if (task.Wait(budget + TimeSpan.FromSeconds(10)))
        {
            var error = cmd.Error ?? "";
            var status = cmd.ExitStatus ?? -1;
            cmd.Dispose();
            return (status, (task.Result + (error.Length > 0 ? "\n" + error : "")).TrimEnd());
        }

        try
        {
            cmd.Dispose();
        }
        catch (Exception ex) when (ex is Renci.SshNet.Common.SshException or IOException or InvalidOperationException)
        {
        }

        return (-1, $"[ssh command on {Name} did not finish within {timeoutSeconds} s: {Truncate(command)}]");
    }

    /// <summary>Runs a command that must succeed; the output is returned, a failure throws with it.</summary>
    public string Sh(string command, int timeoutSeconds = 120)
    {
        var (code, output) = Run(command, timeoutSeconds);
        if (code != 0)
        {
            throw new InvalidOperationException($"{Name}: exit {code} from: {Truncate(command)}\n{output}");
        }

        return output;
    }

    public void Upload(string localPath, string remotePath)
    {
        using var sftp = new SftpClient(_connection);
        sftp.Connect();
        using var file = File.OpenRead(localPath);
        sftp.UploadFile(file, remotePath, canOverride: true);
    }

    public void UploadText(string text, string remotePath)
    {
        using var sftp = new SftpClient(_connection);
        sftp.Connect();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        sftp.UploadFile(stream, remotePath, canOverride: true);
    }

    public byte[]? DownloadBytes(string remotePath)
    {
        try
        {
            using var sftp = new SftpClient(_connection);
            sftp.Connect();
            using var stream = new MemoryStream();
            sftp.DownloadFile(remotePath, stream);
            return stream.ToArray();
        }
        catch (Renci.SshNet.Common.SftpPathNotFoundException)
        {
            return null;
        }
        catch (Renci.SshNet.Common.SftpPermissionDeniedException)
        {
            return null;
        }
    }

    public string? DownloadText(string remotePath)
    {
        var bytes = DownloadBytes(remotePath);
        return bytes is null ? null : Encoding.UTF8.GetString(bytes);
    }

    public void Dispose()
    {
        try
        {
            _ssh.Dispose();
        }
        catch (Exception ex) when (ex is Renci.SshNet.Common.SshException or IOException or InvalidOperationException)
        {
        }
    }

    private static string Truncate(string command) => command.Length <= 160 ? command : command[..160] + "...";
}
