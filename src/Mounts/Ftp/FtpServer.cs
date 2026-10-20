using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging;

namespace AnyFS.Mounts.Ftp;

/// <summary>One collection served by an FTP server, with the login that may see it.</summary>
internal sealed record FtpCollection(string Name, IVirtualFileSystem Fs, IMountStatsSink Stats, string? User, string? Password, bool AllowAnonymous);

/// <summary>Passive mode: the port range (both ends, or neither) and the address advertised to clients behind NAT.</summary>
internal sealed record FtpPassiveOptions(int? MinPort, int? MaxPort, IPAddress? PublicAddress);

/// <summary>
/// AnyFS's own FTP server (spec 8.5): RFC 959 with what today's clients use on top of it (UTF-8 names,
/// REST, SIZE, MDTM, MLSD/MLST, EPSV/EPRT), one process-wide listener per bind address and port shared by
/// every collection mounted there, a login seeing the collections it is entitled to (a named user those
/// configured with it, anonymous those that allow it), every transfer straight through the collection's
/// <see cref="IVirtualFileSystem"/> so the read-only layers and the stats apply. It replaced the FubarDev.FtpServer
/// library on 15-Oct-2026: that library dropped connections under concurrent downloads on a slow machine with a null
/// reference inside its RETR handler, copied files to the data connection a few KB at a time, and has had no release
/// since 2021.
/// </summary>
internal sealed class FtpServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, FtpCollection> _collections = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<FtpSession, byte> _sessions = new();
    private readonly CancellationTokenSource _stopping = new();
    private Task? _accepting;

    private FtpServer(IPAddress address, int port, FtpPassiveOptions passive, ILogger logger)
    {
        Address = address;
        Port = port;
        Passive = passive;
        PassivePorts = new PassivePorts(passive.MinPort, passive.MaxPort);
        _logger = logger;
        _listener = new TcpListener(address, port);
    }

    public IPAddress Address { get; }

    public int Port { get; }

    public FtpPassiveOptions Passive { get; }

    public PassivePorts PassivePorts { get; }

    public ILogger Logger => _logger;

    public int CollectionCount => _collections.Count;

    public int SessionCount => _sessions.Count;

    public static Task<FtpServer> StartAsync(IPAddress address, int port, FtpPassiveOptions passive, ILogger logger, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var server = new FtpServer(address, port, passive, logger);
        try
        {
            server._listener.Start(64);
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException($"could not listen on {address}:{port}: {ex.Message}", ex);
        }

        server._accepting = Task.Run(server.AcceptAsync);
        logger.LogInformation("FTP server listening on {Address}:{Port}", address, port);
        return Task.FromResult(server);
    }

    public void Register(FtpCollection collection)
    {
        if (!_collections.TryAdd(collection.Name, collection))
        {
            throw new InvalidOperationException($"'{collection.Name}' is already served on port {Port} by another mount");
        }
    }

    public void Unregister(string collectionName) => _collections.TryRemove(collectionName, out _);

    /// <summary>The collections a login may see, or null when the login is not valid here.</summary>
    public IReadOnlyList<FtpCollection>? Authenticate(string user, string password)
    {
        var all = _collections.Values.ToList();
        if (user.Equals("anonymous", StringComparison.OrdinalIgnoreCase) || user.Equals("ftp", StringComparison.OrdinalIgnoreCase))
        {
            var open = all.Where(c => c.AllowAnonymous).ToList();
            return open.Count == 0 ? null : open;
        }

        var match = all.Any(c => c.User is not null && c.User.Equals(user, StringComparison.Ordinal) && string.Equals(c.Password ?? "", password, StringComparison.Ordinal));
        return match ? all.Where(c => c.User is not null && c.User.Equals(user, StringComparison.Ordinal)).ToList() : null;
    }

    private async Task AcceptAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                _logger.LogDebug(ex, "ftp accept on {Address}:{Port}", Address, Port);
                continue;
            }

            var session = new FtpSession(this, client);
            _sessions[session] = 0;
            _ = RunSessionAsync(session);
        }
    }

    private async Task RunSessionAsync(FtpSession session)
    {
        try
        {
            await session.RunAsync(_stopping.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!_stopping.IsCancellationRequested)
        {
            // the session ends; the server and its other sessions go on
            _logger.LogWarning(ex, "ftp session on {Address}:{Port} failed", Address, Port);
        }
        catch (Exception)
        {
        }
        finally
        {
            _sessions.TryRemove(session, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        foreach (var session in _sessions.Keys)
        {
            session.Close();
        }

        if (_accepting is not null)
        {
            try
            {
                await _accepting.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
            }
        }

        _stopping.Dispose();
        _logger.LogInformation("FTP server on {Address}:{Port} stopped", Address, Port);
    }
}

/// <summary>
/// The passive-mode data ports: the configured range, handed out round robin so a port whose last connection
/// is still closing is not asked for again at once, or any free port when no range is set.
/// </summary>
internal sealed class PassivePorts(int? from, int? to)
{
    private readonly object _lock = new();
    private int _next = from ?? 0;

    /// <summary>A listening socket on <paramref name="address"/> for one data connection.</summary>
    public TcpListener Listen(IPAddress address)
    {
        if (from is null || to is null)
        {
            var any = new TcpListener(address, 0);
            any.Start(1);
            return any;
        }

        lock (_lock)
        {
            var count = to.Value - from.Value + 1;
            for (var i = 0; i < count; i++)
            {
                var port = _next;
                _next = _next >= to.Value ? from.Value : _next + 1;
                var listener = new TcpListener(address, port);
                try
                {
                    listener.Start(1);
                    return listener;
                }
                catch (SocketException)
                {
                    // in use: the next one
                }
            }
        }

        throw new FtpReplyException(425, $"No free passive port between {from} and {to}");
    }
}

/// <summary>A reply to send instead of carrying on with a command.</summary>
internal sealed class FtpReplyException(int code, string text) : Exception(text)
{
    public int Code { get; } = code;
}
