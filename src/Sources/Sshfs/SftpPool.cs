using System.Net.Sockets;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace AnyFS.Sources.Sshfs;

/// <summary>
/// The source's SFTP connections: up to <see cref="SshfsSettings.Connections"/> clients, handed out round-robin
/// (SSH.NET multiplexes requests on one connection; several connections multiply throughput), each reconnected
/// on demand after it drops. Logging in goes through <see cref="SshLogin"/>: key, password, then
/// keyboard-interactive with the host's prompts; host keys are pinned per source by <see cref="HostKeyStore"/>.
/// </summary>
internal sealed class SftpPool : IAsyncDisposable
{
    private readonly SshfsSettings _settings;
    private readonly ISourceContext _context;
    private readonly HostKeyStore _hostKeys;
    private readonly Slot[] _slots;
    private int _next = -1;
    private bool _disposed;

    public SftpPool(SshfsSettings settings, ISourceContext context, HostKeyStore hostKeys)
    {
        _settings = settings;
        _context = context;
        _hostKeys = hostKeys;
        _slots = Enumerable.Range(0, Math.Clamp(settings.Connections, 1, 16)).Select(_ => new Slot()).ToArray();
    }

    /// <summary>The user's home folder as the server reports it, known once the first connection is up.</summary>
    public string Home { get; private set; } = "/";

    /// <summary>The first connection, made during initialisation so a bad login fails the source with a reason.</summary>
    public Task<SftpClient> ConnectFirstAsync(CancellationToken ct) => GetAsync(_slots[0], ct);

    /// <summary>A connected client, round-robin over the pool; reconnects a dropped one (prompting again if the login needs it).</summary>
    public Task<SftpClient> GetAsync(CancellationToken ct)
    {
        var index = (int)((uint)Interlocked.Increment(ref _next) % (uint)_slots.Length);
        return GetAsync(_slots[index], ct);
    }

    /// <summary>A client whose connection turned out to be dead: dropped, so its slot reconnects on the next use.</summary>
    public void MarkBroken(SftpClient client)
    {
        foreach (var slot in _slots)
        {
            if (ReferenceEquals(slot.Client, client))
            {
                slot.Client = null;
                DisposeQuietly(client);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        foreach (var slot in _slots)
        {
            await slot.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (slot.Client is { } client)
                {
                    slot.Client = null;
                    DisposeQuietly(client);
                }
            }
            finally
            {
                slot.Gate.Release();
            }
        }
    }

    private async Task<SftpClient> GetAsync(Slot slot, CancellationToken ct)
    {
        if (slot.Client is { IsConnected: true } ready)
        {
            return ready;
        }

        await slot.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (slot.Client is { IsConnected: true } connected)
            {
                return connected;
            }

            if (slot.Client is { } stale)
            {
                slot.Client = null;
                DisposeQuietly(stale);
            }

            var login = new SshLogin(_settings, _context, _hostKeys);
            var client = await login.ConnectAsync(ct).ConfigureAwait(false);
            if (ReferenceEquals(slot, _slots[0]) || Home == "/")
            {
                Home = string.IsNullOrEmpty(client.WorkingDirectory) ? "/" : client.WorkingDirectory;
            }

            slot.Client = client;
            return client;
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    private static void DisposeQuietly(SftpClient client)
    {
        try
        {
            client.Dispose();
        }
        catch (Exception ex) when (ex is SshException or SocketException or IOException or ObjectDisposedException or InvalidOperationException)
        {
        }
    }

    private sealed class Slot
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public SftpClient? Client { get; set; }
    }
}

/// <summary>
/// One login: the key file (asking for its passphrase when it needs one and none is set), the password when set,
/// and keyboard-interactive, whose prompts are answered with the saved password once and otherwise put to the
/// user through <see cref="ISourceContext.PromptAsync"/> (a 2FA code, a smart-card PIN, a password that isn't
/// stored). Headless without a console, that becomes "Needs input" as the spec says.
/// </summary>
internal sealed class SshLogin(SshfsSettings settings, ISourceContext context, HostKeyStore hostKeys)
{
    private CancellationToken _ct;
    private bool _passwordOffered;
    private bool _promptCancelled;
    private Exception? _promptFailure;
    private string? _hostKeyProblem;

    public async Task<SftpClient> ConnectAsync(CancellationToken ct)
    {
        _ct = ct;
        var username = settings.Username.Trim();
        var methods = new List<AuthenticationMethod>();
        PrivateKeyFile? key = null;
        if (!string.IsNullOrWhiteSpace(settings.KeyFile))
        {
            key = await LoadKeyAsync(settings.KeyFile.Trim(), ct).ConfigureAwait(false);
            methods.Add(new PrivateKeyAuthenticationMethod(username, [key]));
        }

        if (!string.IsNullOrEmpty(settings.Password))
        {
            methods.Add(new PasswordAuthenticationMethod(username, settings.Password));
        }

        var interactive = new KeyboardInteractiveAuthenticationMethod(username);
        interactive.AuthenticationPrompt += OnAuthenticationPrompt;
        methods.Add(interactive);

        var info = new ConnectionInfo(settings.Host.Trim(), settings.Port, username, methods.ToArray())
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(1, settings.ConnectTimeoutSeconds)),
        };
        var client = new SftpClient(info)
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30),
            OperationTimeout = TimeSpan.FromSeconds(60),
            BufferSize = 256 * 1024,
        };
        client.HostKeyReceived += OnHostKeyReceived;
        try
        {
            // the synchronous Connect on a pool thread: the keyboard-interactive and host-key callbacks run inside it,
            // and a prompt blocks it for as long as the user takes (the server's login grace time is the limit)
            await Task.Run(client.Connect, CancellationToken.None).WaitAsync(ct).ConfigureAwait(false);
            context.Logger.LogInformation("sshfs: connected to {Host}:{Port} as {User}", settings.Host, settings.Port, username);
            return client;
        }
        catch (Exception ex)
        {
            client.Dispose();
            key?.Dispose();
            throw Translate(ex);
        }
    }

    private Exception Translate(Exception ex)
    {
        if (ex is OperationCanceledException)
        {
            return ex;
        }

        if (_hostKeyProblem is not null)
        {
            return new SourceFailedException(_hostKeyProblem, permanent: true, ex);
        }

        if (_promptFailure is not null)
        {
            return _promptFailure;
        }

        if (_promptCancelled)
        {
            return new SourceFailedException($"Needs input: log in to {settings.Host}", permanent: true, ex);
        }

        return ex switch
        {
            SourceFailedException failed => failed,
            SshAuthenticationException => new SourceFailedException($"{settings.Host} refused the login for {settings.Username}: {ex.Message}", permanent: true, ex),
            SocketException socket => new SourceFailedException($"{settings.Host}:{settings.Port} is not reachable: {socket.Message}", permanent: false, ex),
            SshOperationTimeoutException or TimeoutException => new SourceFailedException($"{settings.Host}:{settings.Port} did not answer in {settings.ConnectTimeoutSeconds} s", permanent: false, ex),
            SshConnectionException connection => new SourceFailedException($"{settings.Host} closed the connection: {connection.Message}", permanent: false, ex),
            SshException => new SourceFailedException($"{settings.Host}: {ex.Message}", permanent: false, ex),
            _ => new SourceFailedException($"{settings.Host}: {ex.GetBaseException().Message}", permanent: false, ex),
        };
    }

    private async Task<PrivateKeyFile> LoadKeyAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            throw new SourceFailedException($"The key file was not found: {path}", permanent: true);
        }

        var passphrase = settings.Passphrase;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return string.IsNullOrEmpty(passphrase) ? new PrivateKeyFile(path) : new PrivateKeyFile(path, passphrase);
            }
            catch (SshPassPhraseNullOrEmptyException) when (attempt == 0)
            {
                // the key is encrypted and no passphrase is stored: ask once
                var answer = await context.PromptAsync(new PromptRequest(PromptKind.Password, $"Passphrase for {Path.GetFileName(path)}", $"The private key for {settings.Username}@{settings.Host} is encrypted."), ct).ConfigureAwait(false);
                if (!answer.Answered)
                {
                    throw new SourceFailedException($"Needs input: passphrase for {Path.GetFileName(path)}", permanent: true);
                }

                passphrase = answer.Text ?? "";
            }
            catch (SshException ex)
            {
                throw new SourceFailedException($"The key file could not be read ({ex.Message}); check the file and its passphrase.", permanent: true, ex);
            }
            catch (IOException ex)
            {
                throw new SourceFailedException($"The key file could not be read: {ex.Message}", permanent: true, ex);
            }
        }
    }

    private void OnHostKeyReceived(object? sender, HostKeyEventArgs e)
    {
        var fingerprint = "SHA256:" + e.FingerPrintSHA256;
        var (verdict, stored) = hostKeys.Check(e.HostKeyName, fingerprint);
        switch (verdict)
        {
            case HostKeyVerdict.FirstUse:
                context.Logger.LogInformation("sshfs: trusting {Host}:{Port} on first use ({Algorithm} {Fingerprint})", settings.Host, settings.Port, e.HostKeyName, fingerprint);
                e.CanTrust = true;
                break;
            case HostKeyVerdict.Known:
                e.CanTrust = true;
                break;
            default:
                _hostKeyProblem = $"The host key of {settings.Host}:{settings.Port} changed ({e.HostKeyName}): stored {stored}, offered {fingerprint}. If the server was reinstalled, delete {hostKeys.File} and retry.";
                context.Logger.LogError("sshfs: {Problem}", _hostKeyProblem);
                e.CanTrust = false;
                break;
        }
    }

    private void OnAuthenticationPrompt(object? sender, AuthenticationPromptEventArgs e)
    {
        foreach (var prompt in e.Prompts)
        {
            if (_promptCancelled || _promptFailure is not null)
            {
                prompt.Response = "";
                continue;
            }

            var decision = KeyboardInteractive.Decide(prompt.Request, prompt.IsEchoed, settings.Password, _passwordOffered);
            if (decision == KeyboardInteractive.Answer.SavedPassword)
            {
                _passwordOffered = true;
                prompt.Response = settings.Password!;
                continue;
            }

            try
            {
                var request = new PromptRequest(
                    decision == KeyboardInteractive.Answer.AskHidden ? PromptKind.Password : PromptKind.Text,
                    $"Log in to {settings.Host}",
                    KeyboardInteractive.Message(e.Instruction, prompt.Request, settings.Username));
                var result = context.PromptAsync(request, _ct).GetAwaiter().GetResult();
                if (result.Answered)
                {
                    prompt.Response = result.Text ?? "";
                }
                else
                {
                    _promptCancelled = true;
                    prompt.Response = "";
                }
            }
            catch (Exception ex) when (ex is SourceFailedException or OperationCanceledException)
            {
                // NoPromptHandler (headless) throws "Needs input"; cancellation ends the login
                _promptFailure = ex;
                prompt.Response = "";
            }
        }
    }
}

/// <summary>How a keyboard-interactive prompt is answered (pure, so it can be tested without a server).</summary>
internal static class KeyboardInteractive
{
    public enum Answer
    {
        /// <summary>A password prompt and the settings hold a password that hasn't been tried yet.</summary>
        SavedPassword,
        /// <summary>Ask the user, masked (a password, a PIN, a one-time code).</summary>
        AskHidden,
        /// <summary>Ask the user in the clear (the server echoes it).</summary>
        AskPlain,
    }

    public static Answer Decide(string request, bool echoed, string? savedPassword, bool passwordAlreadyOffered)
    {
        if (!echoed && !passwordAlreadyOffered && !string.IsNullOrEmpty(savedPassword) && LooksLikePassword(request))
        {
            return Answer.SavedPassword;
        }

        return echoed ? Answer.AskPlain : Answer.AskHidden;
    }

    public static string Message(string? instruction, string request, string username)
    {
        var line = request.Trim().TrimEnd(':').Trim();
        var text = string.IsNullOrWhiteSpace(instruction) ? line : instruction.Trim() + "\n" + line;
        return text.Length == 0 ? $"The server is asking {username} for input." : text;
    }

    private static bool LooksLikePassword(string request)
        => request.Contains("password", StringComparison.OrdinalIgnoreCase) || request.Contains("passphrase", StringComparison.OrdinalIgnoreCase);
}

public enum HostKeyVerdict
{
    FirstUse,
    Known,
    Changed,
}

/// <summary>
/// Trust on first use (spec 7.2): the SHA-256 fingerprint of each host-key algorithm the server has presented,
/// one line per algorithm in a text file under the source's state folder. A different key for a known algorithm
/// is a change; a new algorithm (the server gained an ed25519 key) is a first use.
/// </summary>
public sealed class HostKeyStore(string file)
{
    private readonly object _gate = new();

    public string File { get; } = file;

    public (HostKeyVerdict Verdict, string? Stored) Check(string algorithm, string fingerprint)
    {
        lock (_gate)
        {
            var lines = System.IO.File.Exists(File) ? System.IO.File.ReadAllLines(File).ToList() : [];
            foreach (var line in lines)
            {
                var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 2 && parts[0] == algorithm)
                {
                    return parts[1] == fingerprint ? (HostKeyVerdict.Known, parts[1]) : (HostKeyVerdict.Changed, parts[1]);
                }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(File))!);
            lines.Add($"{algorithm} {fingerprint}");
            System.IO.File.WriteAllLines(File, lines);
            return (HostKeyVerdict.FirstUse, null);
        }
    }
}
