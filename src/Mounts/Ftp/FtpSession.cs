using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging;

namespace AnyFS.Mounts.Ftp;

/// <summary>
/// One control connection of <see cref="FtpServer"/>: the command loop, the login, the current
/// directory in the virtual tree (the root lists the login's collections, each collection its tree), the
/// data connection (passive: a listener per transfer from the configured range; active: a connection back
/// to the client's own address only), and the transfers themselves, which read and write the collection's
/// <see cref="IVirtualFileSystem"/> 1 MiB at a time. Commands are handled one at a time; while a transfer
/// runs the control connection is still read, so ABOR (and QUIT) can stop it.
/// </summary>
internal sealed class FtpSession
{
    private const int MaxLineBytes = 8192;
    private const int ReadChunk = 1 << 20;
    private const int WriteChunk = 256 * 1024;
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan DataTimeout = TimeSpan.FromSeconds(30);
    private static readonly UTF8Encoding Utf8 = new(false);

    private readonly FtpServer _server;
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly ILogger _logger;
    private readonly IPEndPoint _peer;
    private readonly IPAddress _localAddress;
    private readonly CancellationTokenSource _closed = new();
    private readonly byte[] _lineBuffer = new byte[MaxLineBytes];
    private int _lineLength;
    private int _lineEnd;
    private Task<string?>? _pendingLine;

    private string? _pendingUser;
    private string? _login;
    private IReadOnlyList<FtpCollection> _visible = [];
    private string _cwd = "/";
    private long _restart;
    private Target? _renameFrom;
    private TcpListener? _passive;
    private IPEndPoint? _active;
    private bool _epsvAll;
    private bool _quit;

    public FtpSession(FtpServer server, TcpClient client)
    {
        _server = server;
        _client = client;
        _logger = server.Logger;
        _stream = client.GetStream();
        _peer = (IPEndPoint)client.Client.RemoteEndPoint!;
        _localAddress = ((IPEndPoint)client.Client.LocalEndPoint!).Address;
        if (_localAddress.IsIPv4MappedToIPv6)
        {
            _localAddress = _localAddress.MapToIPv4();
        }
    }

    /// <summary>Where a path points: the virtual root, or a path inside a collection.</summary>
    private sealed record Target(FtpCollection? Collection, string Path, string Virtual)
    {
        public bool IsRoot => Collection is null;

        public IVirtualFileSystem Fs => Collection!.Fs;
    }

    public void Close()
    {
        _closed.Cancel();
        try
        {
            _client.Close();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public async Task RunAsync(CancellationToken serverStopping)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(serverStopping, _closed.Token);
        var ct = linked.Token;
        _logger.LogDebug("ftp {Peer}: connected", _peer);
        try
        {
            await ReplyAsync(220, "AnyFS FTP server ready", ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested && !_quit)
            {
                var line = await NextLineAsync(ct).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                await HandleAsync(line, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException ex)
        {
            _logger.LogDebug("ftp {Peer}: connection ended: {Message}", _peer, ex.GetBaseException().Message);
        }
        catch (SocketException ex)
        {
            _logger.LogDebug("ftp {Peer}: connection ended: {Message}", _peer, ex.Message);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ftp {Peer}: session failed", _peer);
        }
        finally
        {
            Logout();
            _passive?.Stop();
            _client.Dispose();
            _logger.LogDebug("ftp {Peer}: closed", _peer);
        }
    }

    // ---- the control connection ----------------------------------------------------------------------------

    /// <summary>The next command line, or null when the client went away or said nothing for the idle timeout.</summary>
    private async Task<string?> NextLineAsync(CancellationToken ct)
    {
        var pending = _pendingLine;
        _pendingLine = null;
        if (pending is null)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idle.CancelAfter(IdleTimeout);
            try
            {
                return await ReadLineAsync(idle.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await TryReplyAsync(421, "Idle timeout, closing control connection", ct).ConfigureAwait(false);
                return null;
            }
        }

        return await pending.ConfigureAwait(false);
    }

    /// <summary>One line of the control connection, CR LF or LF terminated, Telnet IAC sequences dropped, as UTF-8.</summary>
    private async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        var line = new List<byte>(128);
        while (true)
        {
            if (_lineLength == _lineEnd)
            {
                var n = await _stream.ReadAsync(_lineBuffer, ct).ConfigureAwait(false);
                if (n == 0)
                {
                    return line.Count == 0 ? null : Utf8.GetString(line.ToArray());
                }

                _lineLength = 0;
                _lineEnd = n;
            }

            var b = _lineBuffer[_lineLength++];
            if (b == '\n')
            {
                if (line.Count > 0 && line[^1] == '\r')
                {
                    line.RemoveAt(line.Count - 1);
                }

                return Utf8.GetString(line.ToArray());
            }

            if (b == 0xFF)
            {
                // Telnet IAC: IP, DM, DO/DONT/WILL/WONT with an option byte
                var cmd = await NextByteAsync(ct).ConfigureAwait(false);
                if (cmd is >= 0xFB and <= 0xFE)
                {
                    await NextByteAsync(ct).ConfigureAwait(false);
                }

                continue;
            }

            if (line.Count >= MaxLineBytes)
            {
                throw new FtpReplyException(500, "Line too long");
            }

            line.Add(b);
        }
    }

    private async Task<int> NextByteAsync(CancellationToken ct)
    {
        if (_lineLength == _lineEnd)
        {
            var n = await _stream.ReadAsync(_lineBuffer, ct).ConfigureAwait(false);
            if (n == 0)
            {
                return -1;
            }

            _lineLength = 0;
            _lineEnd = n;
        }

        return _lineBuffer[_lineLength++];
    }

    private Task ReplyAsync(int code, string text, CancellationToken ct)
    {
        _logger.LogDebug("ftp {Peer} < {Code} {Text}", _peer, code, text);
        return _stream.WriteAsync(Utf8.GetBytes($"{code} {text}\r\n"), ct).AsTask();
    }

    private Task ReplyLinesAsync(int code, string first, IEnumerable<string> lines, string last, CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.Append(code).Append('-').Append(first).Append("\r\n");
        foreach (var line in lines)
        {
            sb.Append(' ').Append(line).Append("\r\n");
        }

        sb.Append(code).Append(' ').Append(last).Append("\r\n");
        _logger.LogDebug("ftp {Peer} < {Code}-{Text} ... {Last}", _peer, code, first, last);
        return _stream.WriteAsync(Utf8.GetBytes(sb.ToString()), ct).AsTask();
    }

    private async Task TryReplyAsync(int code, string text, CancellationToken ct)
    {
        try
        {
            await ReplyAsync(code, text, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException or TimeoutException)
        {
        }
    }

    // ---- commands ---------------------------------------------------------------------------------------------

    private async Task HandleAsync(string line, CancellationToken ct)
    {
        var space = line.IndexOf(' ');
        var verb = (space < 0 ? line : line[..space]).ToUpperInvariant();
        var arg = space < 0 ? "" : line[(space + 1)..].Trim();
        _logger.LogDebug("ftp {Peer} > {Verb} {Arg}", _peer, verb, verb == "PASS" ? "****" : arg);
        try
        {
            if (_login is null && verb is not ("USER" or "PASS" or "QUIT" or "FEAT" or "SYST" or "NOOP" or "OPTS" or "AUTH" or "HELP" or "CLNT"))
            {
                throw new FtpReplyException(530, "Not logged in");
            }

            switch (verb)
            {
                case "USER":
                    _pendingUser = arg;
                    Logout();
                    await ReplyAsync(331, $"Password required for {arg}", ct).ConfigureAwait(false);
                    break;
                case "PASS":
                    await PassAsync(arg, ct).ConfigureAwait(false);
                    break;
                case "QUIT":
                    _quit = true;
                    await ReplyAsync(221, "Goodbye", ct).ConfigureAwait(false);
                    break;
                case "SYST":
                    await ReplyAsync(215, "UNIX Type: L8", ct).ConfigureAwait(false);
                    break;
                case "FEAT":
                    await ReplyLinesAsync(211, "Features:", ["UTF8", "REST STREAM", "SIZE", "MDTM", "MLST type*;size*;modify*;perm*;", "MLSD", "EPSV", "EPRT", "PASV", "TVFS"], "End", ct).ConfigureAwait(false);
                    break;
                case "OPTS":
                    await OptsAsync(arg, ct).ConfigureAwait(false);
                    break;
                case "NOOP":
                    await ReplyAsync(200, "OK", ct).ConfigureAwait(false);
                    break;
                case "CLNT":
                    await ReplyAsync(200, "Noted", ct).ConfigureAwait(false);
                    break;
                case "HELP":
                    await ReplyLinesAsync(214, "The following commands are recognized:", ["USER PASS QUIT SYST FEAT OPTS NOOP PWD CWD CDUP TYPE MODE STRU", "PASV EPSV PORT EPRT LIST NLST MLSD MLST STAT SIZE MDTM", "RETR STOR APPE REST ABOR DELE RMD MKD RNFR RNTO ALLO HELP"], "Help OK", ct).ConfigureAwait(false);
                    break;
                case "AUTH":
                case "ADAT":
                case "PBSZ":
                case "PROT":
                case "CCC":
                    await ReplyAsync(502, "TLS is not supported", ct).ConfigureAwait(false);
                    break;
                case "PWD":
                case "XPWD":
                    await ReplyAsync(257, $"\"{_cwd.Replace("\"", "\"\"")}\" is the current directory", ct).ConfigureAwait(false);
                    break;
                case "CWD":
                case "XCWD":
                    await CwdAsync(arg, ct).ConfigureAwait(false);
                    break;
                case "CDUP":
                case "XCUP":
                    await CwdAsync("..", ct).ConfigureAwait(false);
                    break;
                case "TYPE":
                    await TypeAsync(arg, ct).ConfigureAwait(false);
                    break;
                case "MODE":
                    await ReplyAsync(arg.Equals("S", StringComparison.OrdinalIgnoreCase) ? 200 : 504, arg.Equals("S", StringComparison.OrdinalIgnoreCase) ? "Mode set to S" : "Only stream mode is supported", ct).ConfigureAwait(false);
                    break;
                case "STRU":
                    await ReplyAsync(arg.Equals("F", StringComparison.OrdinalIgnoreCase) ? 200 : 504, arg.Equals("F", StringComparison.OrdinalIgnoreCase) ? "Structure set to F" : "Only file structure is supported", ct).ConfigureAwait(false);
                    break;
                case "ALLO":
                    await ReplyAsync(202, "No storage allocation necessary", ct).ConfigureAwait(false);
                    break;
                case "PASV":
                    await PasvAsync(ct).ConfigureAwait(false);
                    break;
                case "EPSV":
                    await EpsvAsync(arg, ct).ConfigureAwait(false);
                    break;
                case "PORT":
                    await PortAsync(arg, ct).ConfigureAwait(false);
                    break;
                case "EPRT":
                    await EprtAsync(arg, ct).ConfigureAwait(false);
                    break;
                case "REST":
                    await RestAsync(arg, ct).ConfigureAwait(false);
                    break;
                case "LIST":
                    await ListAsync(arg, ListingStyle.Long, ct).ConfigureAwait(false);
                    break;
                case "NLST":
                    await ListAsync(arg, ListingStyle.Names, ct).ConfigureAwait(false);
                    break;
                case "MLSD":
                    await ListAsync(arg, ListingStyle.Facts, ct).ConfigureAwait(false);
                    break;
                case "MLST":
                    await MlstAsync(arg, ct).ConfigureAwait(false);
                    break;
                case "STAT":
                    await StatAsync(arg, ct).ConfigureAwait(false);
                    break;
                case "SIZE":
                    await SizeAsync(arg, ct).ConfigureAwait(false);
                    break;
                case "MDTM":
                    await MdtmAsync(arg, ct).ConfigureAwait(false);
                    break;
                case "RETR":
                    await RetrAsync(arg, ct).ConfigureAwait(false);
                    break;
                case "STOR":
                    await StorAsync(arg, append: false, ct).ConfigureAwait(false);
                    break;
                case "APPE":
                    await StorAsync(arg, append: true, ct).ConfigureAwait(false);
                    break;
                case "ABOR":
                    await ReplyAsync(226, "No transfer to abort", ct).ConfigureAwait(false);
                    break;
                case "DELE":
                    await DeleteAsync(arg, directory: false, ct).ConfigureAwait(false);
                    break;
                case "RMD":
                case "XRMD":
                    await DeleteAsync(arg, directory: true, ct).ConfigureAwait(false);
                    break;
                case "MKD":
                case "XMKD":
                    await MkdAsync(arg, ct).ConfigureAwait(false);
                    break;
                case "RNFR":
                    await RnfrAsync(arg, ct).ConfigureAwait(false);
                    break;
                case "RNTO":
                    await RntoAsync(arg, ct).ConfigureAwait(false);
                    break;
                case "SITE":
                    await ReplyAsync(502, "SITE commands are not supported", ct).ConfigureAwait(false);
                    break;
                default:
                    await ReplyAsync(500, $"Unknown command {verb}", ct).ConfigureAwait(false);
                    break;
            }
        }
        catch (FtpReplyException ex)
        {
            await ReplyAsync(ex.Code, ex.Message, ct).ConfigureAwait(false);
        }
        catch (VfsException ex)
        {
            var (code, text) = Translate(ex);
            await ReplyAsync(code, text, ct).ConfigureAwait(false);
        }
    }

    private async Task PassAsync(string password, CancellationToken ct)
    {
        if (_pendingUser is null)
        {
            throw new FtpReplyException(503, "Send USER first");
        }

        var visible = _server.Authenticate(_pendingUser, password);
        if (visible is null)
        {
            _logger.LogInformation("ftp {Peer}: login for '{User}' refused", _peer, _pendingUser);
            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false); // no password guessing at line rate
            throw new FtpReplyException(530, "Login incorrect");
        }

        _login = _pendingUser.Equals("ftp", StringComparison.OrdinalIgnoreCase) ? "anonymous" : _pendingUser;
        _visible = visible;
        _cwd = "/";
        foreach (var collection in _visible)
        {
            collection.Stats.ConnectionOpened();
        }

        _logger.LogInformation("ftp {Peer}: {User} logged in, sees {Collections}", _peer, _login, string.Join(", ", visible.Select(c => c.Name)));
        await ReplyAsync(230, $"User {_login} logged in", ct).ConfigureAwait(false);
    }

    private void Logout()
    {
        if (_login is null)
        {
            return;
        }

        foreach (var collection in _visible)
        {
            collection.Stats.ConnectionClosed();
        }

        _login = null;
        _visible = [];
        _cwd = "/";
    }

    private Task OptsAsync(string arg, CancellationToken ct)
    {
        var upper = arg.ToUpperInvariant();
        if (upper is "UTF8 ON" or "UTF8" or "UTF-8 ON")
        {
            return ReplyAsync(200, "UTF-8 is always on", ct);
        }

        if (upper.StartsWith("MLST", StringComparison.Ordinal))
        {
            return ReplyAsync(200, "MLST OPTS type;size;modify;perm;", ct);
        }

        return ReplyAsync(501, "Option not understood", ct);
    }

    private Task TypeAsync(string arg, CancellationToken ct)
    {
        var upper = arg.ToUpperInvariant();
        if (upper is "I" or "L 8" or "L8")
        {
            return ReplyAsync(200, "Type set to I", ct);
        }

        if (upper is "A" or "A N")
        {
            // text files travel as they are: every client of note asks for I before a file transfer
            return ReplyAsync(200, "Type set to A", ct);
        }

        return ReplyAsync(504, "Only types A and I are supported", ct);
    }

    private async Task CwdAsync(string arg, CancellationToken ct)
    {
        var target = Resolve(arg.Length == 0 ? "/" : arg);
        if (!target.IsRoot)
        {
            var entry = await target.Fs.StatAsync(target.Path, ct).ConfigureAwait(false) ?? throw new FtpReplyException(550, "No such file or directory");
            if (!entry.IsDirectory)
            {
                throw new FtpReplyException(550, "Not a directory");
            }
        }

        _cwd = target.Virtual;
        await ReplyAsync(250, $"Directory changed to {_cwd}", ct).ConfigureAwait(false);
    }

    private Task RestAsync(string arg, CancellationToken ct)
    {
        if (!long.TryParse(arg, NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
        {
            return ReplyAsync(501, "REST needs a byte offset", ct);
        }

        _restart = offset;
        return ReplyAsync(350, $"Restarting at {offset}. Send RETR or STOR", ct);
    }

    // ---- paths ------------------------------------------------------------------------------------------------

    /// <summary>
    /// A path argument against the current directory: "/" is the root that lists the login's collections, the
    /// first segment under it a collection, the rest a path in that collection's tree.
    /// </summary>
    private Target Resolve(string arg)
    {
        var segments = new List<string>();
        var full = arg.StartsWith('/') ? arg : _cwd + "/" + arg;
        foreach (var segment in full.Split('/'))
        {
            switch (segment)
            {
                case "" or ".":
                    continue;
                case "..":
                    if (segments.Count > 0)
                    {
                        segments.RemoveAt(segments.Count - 1);
                    }

                    continue;
                default:
                    segments.Add(segment);
                    break;
            }
        }

        if (segments.Count == 0)
        {
            return new Target(null, "", "/");
        }

        var collection = _visible.FirstOrDefault(c => c.Name.Equals(segments[0], StringComparison.OrdinalIgnoreCase))
            ?? throw new FtpReplyException(550, "No such file or directory");
        if (!VfsPath.TryNormalize(string.Join('/', segments.Skip(1)), out var path, out var reason))
        {
            throw new FtpReplyException(553, "Invalid file name: " + reason);
        }

        return new Target(collection, path, "/" + collection.Name + (path.Length == 0 ? "" : "/" + path));
    }

    private static Target NotRoot(Target target, string what)
        => target.IsRoot ? throw new FtpReplyException(553, $"The server root only lists collections; {what} inside one") : target;

    private static (int Code, string Text) Translate(VfsException ex) => ex.Error switch
    {
        VfsError.NotFound => (550, "No such file or directory"),
        VfsError.AlreadyExists => (550, "File exists"),
        VfsError.NotADirectory => (550, "Not a directory"),
        VfsError.IsADirectory => (550, "Is a directory"),
        VfsError.DirectoryNotEmpty => (550, "Directory not empty"),
        VfsError.ReadOnly => (550, "Permission denied: " + ex.Message),
        VfsError.AccessDenied => (550, "Permission denied"),
        VfsError.InvalidPath => (553, "Invalid file name: " + ex.Message),
        VfsError.NotSupported => (502, ex.Message),
        VfsError.Unavailable => (450, "Not available right now: " + ex.Message),
        _ => (451, "Local error: " + ex.Message),
    };

    // ---- the data connection ----------------------------------------------------------------------------------

    private async Task PasvAsync(CancellationToken ct)
    {
        if (_epsvAll)
        {
            throw new FtpReplyException(501, "EPSV ALL was sent; use EPSV");
        }

        var advertised = _server.Passive.PublicAddress ?? _localAddress;
        if (advertised.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new FtpReplyException(522, "Network protocol not supported, use (2)");
        }

        var port = OpenPassive();
        var h = advertised.GetAddressBytes();
        await ReplyAsync(227, $"Entering Passive Mode ({h[0]},{h[1]},{h[2]},{h[3]},{port / 256},{port % 256})", ct).ConfigureAwait(false);
    }

    private async Task EpsvAsync(string arg, CancellationToken ct)
    {
        if (arg.Equals("ALL", StringComparison.OrdinalIgnoreCase))
        {
            _epsvAll = true;
            await ReplyAsync(200, "EPSV ALL OK", ct).ConfigureAwait(false);
            return;
        }

        var port = OpenPassive();
        await ReplyAsync(229, $"Entering Extended Passive Mode (|||{port}|)", ct).ConfigureAwait(false);
    }

    private int OpenPassive()
    {
        _passive?.Stop();
        _active = null;
        _passive = _server.PassivePorts.Listen(_localAddress);
        return ((IPEndPoint)_passive.LocalEndpoint).Port;
    }

    private Task PortAsync(string arg, CancellationToken ct)
    {
        if (_epsvAll)
        {
            throw new FtpReplyException(501, "EPSV ALL was sent; use EPSV");
        }

        var parts = arg.Split(',');
        if (parts.Length != 6 || !parts.All(p => byte.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
        {
            throw new FtpReplyException(501, "PORT needs h1,h2,h3,h4,p1,p2");
        }

        var bytes = parts.Select(p => byte.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        return SetActiveAsync(new IPAddress(bytes[..4]), bytes[4] * 256 + bytes[5], ct);
    }

    private Task EprtAsync(string arg, CancellationToken ct)
    {
        if (_epsvAll)
        {
            throw new FtpReplyException(501, "EPSV ALL was sent; use EPSV");
        }

        // |1|h.h.h.h|port| or |2|h:h::h|port|, with any delimiter the client picks
        if (arg.Length < 7)
        {
            throw new FtpReplyException(501, "EPRT needs |protocol|address|port|");
        }

        var parts = arg.Split(arg[0]);
        if (parts.Length < 5 || !IPAddress.TryParse(parts[2], out var address) || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var port))
        {
            throw new FtpReplyException(501, "EPRT needs |protocol|address|port|");
        }

        return SetActiveAsync(address, port, ct);
    }

    private Task SetActiveAsync(IPAddress address, int port, CancellationToken ct)
    {
        var peer = _peer.Address.IsIPv4MappedToIPv6 ? _peer.Address.MapToIPv4() : _peer.Address;
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (!address.Equals(peer) || port < 1024)
        {
            // no bounce attacks: the data connection goes to the client itself, on an unprivileged port
            throw new FtpReplyException(501, "The data address must be your own, port 1024 or above");
        }

        _passive?.Stop();
        _passive = null;
        _active = new IPEndPoint(address, port);
        return ReplyAsync(200, "Data address noted", ct);
    }

    /// <summary>The data connection for the transfer about to start: accepted on the passive listener, or made to the client.</summary>
    private async Task<TcpClient> OpenDataAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(DataTimeout);
        if (_passive is { } listener)
        {
            _passive = null;
            try
            {
                while (true)
                {
                    var data = await listener.AcceptTcpClientAsync(timeout.Token).ConfigureAwait(false);
                    var from = ((IPEndPoint)data.Client.RemoteEndPoint!).Address;
                    if (SameHost(from, _peer.Address))
                    {
                        return data;
                    }

                    // somebody else found the port: not their transfer
                    _logger.LogWarning("ftp {Peer}: data connection from {Other} refused", _peer, from);
                    data.Dispose();
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new FtpReplyException(425, "No data connection within 30 s");
            }
            finally
            {
                listener.Stop();
            }
        }

        if (_active is { } endpoint)
        {
            _active = null;
            var data = new TcpClient(endpoint.AddressFamily);
            try
            {
                await data.ConnectAsync(endpoint, timeout.Token).ConfigureAwait(false);
                return data;
            }
            catch (Exception ex) when (ex is SocketException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                data.Dispose();
                throw new FtpReplyException(425, $"Could not connect to {endpoint}");
            }
        }

        throw new FtpReplyException(425, "Use PASV, EPSV, PORT or EPRT first");
    }

    private static bool SameHost(IPAddress a, IPAddress b)
    {
        if (a.IsIPv4MappedToIPv6)
        {
            a = a.MapToIPv4();
        }

        if (b.IsIPv4MappedToIPv6)
        {
            b = b.MapToIPv4();
        }

        return a.Equals(b);
    }

    /// <summary>
    /// Runs a transfer on the data connection while the control connection stays read, so ABOR (or QUIT) can
    /// stop it: 150 first, then 226 when it finished, 426 when the client aborted or the data connection broke,
    /// or the error's own reply.
    /// </summary>
    private async Task TransferAsync(string opening, Func<Stream, CancellationToken, Task> body, CancellationToken ct)
    {
        using var data = await OpenDataAsync(ct).ConfigureAwait(false);
        await ReplyAsync(150, opening, ct).ConfigureAwait(false);
        using var aborting = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var aborted = false;
        var transfer = Task.Run(() => body(data.GetStream(), aborting.Token), CancellationToken.None);
        while (true)
        {
            _pendingLine ??= ReadLineAsync(ct);
            var finished = await Task.WhenAny(transfer, _pendingLine).ConfigureAwait(false);
            if (finished == transfer)
            {
                break;
            }

            var line = await _pendingLine.ConfigureAwait(false);
            _pendingLine = null;
            if (line is null)
            {
                aborted = true;
                await aborting.CancelAsync().ConfigureAwait(false);
                break;
            }

            var verb = line.Split(' ', 2)[0].ToUpperInvariant();
            _logger.LogDebug("ftp {Peer} > {Verb} (during a transfer)", _peer, verb);
            switch (verb)
            {
                case "ABOR":
                case "QUIT":
                    aborted = true;
                    _quit = verb == "QUIT";
                    await aborting.CancelAsync().ConfigureAwait(false);
                    break;
                case "NOOP":
                    await ReplyAsync(200, "OK", ct).ConfigureAwait(false);
                    continue;
                case "STAT":
                    await ReplyAsync(211, "Transfer in progress", ct).ConfigureAwait(false);
                    continue;
                default:
                    await ReplyAsync(503, "Transfer in progress; ABOR stops it", ct).ConfigureAwait(false);
                    continue;
            }

            break;
        }

        try
        {
            await transfer.ConfigureAwait(false);
            data.Client.Shutdown(SocketShutdown.Both);
            await ReplyAsync(226, "Transfer complete", ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (aborted)
        {
            await ReplyAsync(426, "Transfer aborted", ct).ConfigureAwait(false);
            await ReplyAsync(226, "Abort complete", ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            _logger.LogDebug("ftp {Peer}: data connection ended: {Message}", _peer, ex.GetBaseException().Message);
            await ReplyAsync(426, "Data connection closed; transfer aborted", ct).ConfigureAwait(false);
        }
        catch (VfsException ex)
        {
            var (code, text) = Translate(ex);
            await ReplyAsync(code == 550 ? 451 : code, text, ct).ConfigureAwait(false);
        }
        finally
        {
            _restart = 0;
        }
    }

    // ---- listings ------------------------------------------------------------------------------------------

    private enum ListingStyle
    {
        Long,
        Names,
        Facts,
    }

    private async Task ListAsync(string arg, ListingStyle style, CancellationToken ct)
    {
        // "LIST -la path": ls options from clients that think they are talking to one
        while (arg.StartsWith('-'))
        {
            var space = arg.IndexOf(' ');
            arg = space < 0 ? "" : arg[(space + 1)..].TrimStart();
        }

        var target = Resolve(arg.Length == 0 ? "." : arg);
        var entries = await EntriesAsync(target, ct).ConfigureAwait(false);
        var text = new StringBuilder();
        foreach (var (entry, writable) in entries)
        {
            text.Append(style switch
            {
                ListingStyle.Names => entry.Name,
                ListingStyle.Facts => Facts(entry, writable),
                _ => LongLine(entry, writable),
            }).Append("\r\n");
        }

        var bytes = Utf8.GetBytes(text.ToString());
        await TransferAsync($"Opening data connection for {style switch { ListingStyle.Facts => "MLSD", ListingStyle.Names => "NLST", _ => "LIST" }}", (stream, token) => stream.WriteAsync(bytes, token).AsTask(), ct).ConfigureAwait(false);
    }

    /// <summary>The entries at a target: the login's collections at the root, a folder's children, or a file's own entry.</summary>
    private async Task<List<(VfsEntry Entry, bool Writable)>> EntriesAsync(Target target, CancellationToken ct)
    {
        var list = new List<(VfsEntry, bool)>();
        if (target.IsRoot)
        {
            foreach (var collection in _visible.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            {
                list.Add((VfsEntry.Directory(collection.Name), false));
            }

            return list;
        }

        var entry = await target.Fs.StatAsync(target.Path, ct).ConfigureAwait(false) ?? throw new FtpReplyException(550, "No such file or directory");
        if (!entry.IsDirectory)
        {
            list.Add((entry, target.Fs.CanWrite(target.Path)));
            return list;
        }

        await foreach (var child in target.Fs.ListAsync(target.Path, ct).ConfigureAwait(false))
        {
            list.Add((child, target.Fs.CanWrite(VfsPath.Join(target.Path, child.Name))));
        }

        return list;
    }

    /// <summary>A line of LIST, in the form every client parses: ls -l with one link, anyfs as owner and group.</summary>
    private static string LongLine(VfsEntry entry, bool writable)
    {
        var permissions = entry.IsDirectory ? (writable ? "drwxr-xr-x" : "dr-xr-xr-x") : (writable ? "-rw-r--r--" : "-r--r--r--");
        var modified = (entry.Modified ?? DateTimeOffset.UtcNow).UtcDateTime;
        var age = DateTime.UtcNow - modified;
        var stamp = age.TotalDays is < 182 and > -1
            ? modified.ToString("MMM dd HH:mm", CultureInfo.InvariantCulture)
            : modified.ToString("MMM dd  yyyy", CultureInfo.InvariantCulture);
        return $"{permissions} 1 anyfs anyfs {entry.Length,12} {stamp} {entry.Name}";
    }

    /// <summary>An MLSD/MLST fact line (RFC 3659).</summary>
    private static string Facts(VfsEntry entry, bool writable)
    {
        var modified = (entry.Modified ?? DateTimeOffset.UtcNow).UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        return entry.IsDirectory
            ? $"type=dir;modify={modified};perm={(writable ? "cdelmfp" : "el")}; {entry.Name}"
            : $"type=file;size={entry.Length};modify={modified};perm={(writable ? "adfrw" : "r")}; {entry.Name}";
    }

    private async Task MlstAsync(string arg, CancellationToken ct)
    {
        var target = Resolve(arg.Length == 0 ? "." : arg);
        VfsEntry entry;
        var writable = false;
        if (target.IsRoot)
        {
            entry = VfsEntry.Directory("/");
        }
        else
        {
            entry = await target.Fs.StatAsync(target.Path, ct).ConfigureAwait(false) ?? throw new FtpReplyException(550, "No such file or directory");
            writable = target.Fs.CanWrite(target.Path);
        }

        var facts = Facts(entry with { Name = target.Virtual }, writable);
        await ReplyLinesAsync(250, "Listing " + target.Virtual, [facts], "End", ct).ConfigureAwait(false);
    }

    private async Task StatAsync(string arg, CancellationToken ct)
    {
        if (arg.Length == 0)
        {
            await ReplyLinesAsync(211, "AnyFS FTP server status:", [$"Logged in as {_login}", $"Current directory {_cwd}", "TYPE: I, STRU: F, MODE: S"], "End of status", ct).ConfigureAwait(false);
            return;
        }

        var entries = await EntriesAsync(Resolve(arg), ct).ConfigureAwait(false);
        await ReplyLinesAsync(213, "Status of " + arg + ":", entries.Select(e => LongLine(e.Entry, e.Writable)), "End of status", ct).ConfigureAwait(false);
    }

    private async Task SizeAsync(string arg, CancellationToken ct)
    {
        var target = NotRoot(Resolve(arg), "files are");
        var entry = await target.Fs.StatAsync(target.Path, ct).ConfigureAwait(false) ?? throw new FtpReplyException(550, "No such file or directory");
        if (entry.IsDirectory)
        {
            throw new FtpReplyException(550, "Is a directory");
        }

        await ReplyAsync(213, entry.Length.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
    }

    private async Task MdtmAsync(string arg, CancellationToken ct)
    {
        var target = NotRoot(Resolve(arg), "files are");
        var entry = await target.Fs.StatAsync(target.Path, ct).ConfigureAwait(false) ?? throw new FtpReplyException(550, "No such file or directory");
        await ReplyAsync(213, (entry.Modified ?? DateTimeOffset.UtcNow).UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
    }

    // ---- transfers -----------------------------------------------------------------------------------------

    private async Task RetrAsync(string arg, CancellationToken ct)
    {
        var target = NotRoot(Resolve(arg), "files are");
        var entry = await target.Fs.StatAsync(target.Path, ct).ConfigureAwait(false) ?? throw new FtpReplyException(550, "No such file or directory");
        if (entry.IsDirectory)
        {
            throw new FtpReplyException(550, "Is a directory");
        }

        var offset = _restart;
        var file = await target.Fs.OpenAsync(target.Path, VfsOpenMode.Read, ct).ConfigureAwait(false);
        try
        {
            await TransferAsync($"Opening BINARY mode data connection for {entry.Name} ({entry.Length} bytes)", async (stream, token) =>
            {
                var buffer = ArrayPool<byte>.Shared.Rent(ReadChunk);
                try
                {
                    var position = offset;
                    while (true)
                    {
                        var n = await file.ReadAsync(position, buffer.AsMemory(0, ReadChunk), token).ConfigureAwait(false);
                        if (n <= 0)
                        {
                            break;
                        }

                        await stream.WriteAsync(buffer.AsMemory(0, n), token).ConfigureAwait(false);
                        position += n;
                    }

                    await stream.FlushAsync(token).ConfigureAwait(false);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            await file.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task StorAsync(string arg, bool append, CancellationToken ct)
    {
        var target = NotRoot(Resolve(arg), "files go");
        if (!target.Fs.CanWrite(target.Path))
        {
            throw new FtpReplyException(550, "Permission denied: the collection, the source or the mount is read-only");
        }

        var offset = _restart;
        var mode = append ? VfsOpenMode.OpenOrCreate : offset > 0 ? VfsOpenMode.ReadWrite : VfsOpenMode.Create;
        var file = await target.Fs.OpenAsync(target.Path, mode, ct).ConfigureAwait(false);
        try
        {
            if (append)
            {
                offset = file.Length;
            }

            await TransferAsync($"Opening BINARY mode data connection for {VfsPath.Name(target.Path)}", async (stream, token) =>
            {
                var buffer = ArrayPool<byte>.Shared.Rent(WriteChunk);
                try
                {
                    var position = offset;
                    while (true)
                    {
                        var n = await stream.ReadAsync(buffer.AsMemory(0, WriteChunk), token).ConfigureAwait(false);
                        if (n == 0)
                        {
                            break;
                        }

                        await file.WriteAsync(position, buffer.AsMemory(0, n), token).ConfigureAwait(false);
                        position += n;
                    }

                    await file.FlushAsync(token).ConfigureAwait(false);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            await file.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ---- the tree ------------------------------------------------------------------------------------------

    private async Task DeleteAsync(string arg, bool directory, CancellationToken ct)
    {
        var target = NotRoot(Resolve(arg), "things live");
        if (target.Path.Length == 0)
        {
            throw new FtpReplyException(553, "A collection cannot be deleted through FTP");
        }

        var entry = await target.Fs.StatAsync(target.Path, ct).ConfigureAwait(false) ?? throw new FtpReplyException(550, "No such file or directory");
        if (entry.IsDirectory != directory)
        {
            throw new FtpReplyException(550, directory ? "Not a directory" : "Is a directory");
        }

        await target.Fs.DeleteAsync(target.Path, ct).ConfigureAwait(false);
        await ReplyAsync(250, directory ? "Directory removed" : "File deleted", ct).ConfigureAwait(false);
    }

    private async Task MkdAsync(string arg, CancellationToken ct)
    {
        var target = NotRoot(Resolve(arg), "folders go");
        if (target.Path.Length == 0)
        {
            throw new FtpReplyException(550, "File exists");
        }

        await target.Fs.CreateDirectoryAsync(target.Path, ct).ConfigureAwait(false);
        await ReplyAsync(257, $"\"{target.Virtual.Replace("\"", "\"\"")}\" created", ct).ConfigureAwait(false);
    }

    private async Task RnfrAsync(string arg, CancellationToken ct)
    {
        var target = NotRoot(Resolve(arg), "things live");
        if (target.Path.Length == 0)
        {
            throw new FtpReplyException(553, "A collection cannot be renamed through FTP");
        }

        _ = await target.Fs.StatAsync(target.Path, ct).ConfigureAwait(false) ?? throw new FtpReplyException(550, "No such file or directory");
        _renameFrom = target;
        await ReplyAsync(350, "Ready for RNTO", ct).ConfigureAwait(false);
    }

    private async Task RntoAsync(string arg, CancellationToken ct)
    {
        var from = _renameFrom ?? throw new FtpReplyException(503, "Send RNFR first");
        _renameFrom = null;
        var to = NotRoot(Resolve(arg), "things live");
        if (to.Path.Length == 0 || !ReferenceEquals(to.Collection, from.Collection))
        {
            throw new FtpReplyException(553, "A rename stays inside its collection");
        }

        await from.Fs.RenameAsync(from.Path, to.Path, replace: true, ct).ConfigureAwait(false);
        await ReplyAsync(250, "Rename successful", ct).ConfigureAwait(false);
    }
}
