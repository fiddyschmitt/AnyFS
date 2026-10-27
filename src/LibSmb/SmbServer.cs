using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LibSmb;

/// <summary>How an <see cref="SmbServer"/> listens and whom it lets in.</summary>
public sealed class SmbServerOptions
{
    /// <summary>The address and port to listen on; port 0 takes a free one (see <see cref="SmbServer.LocalEndPoint"/>).</summary>
    public IPEndPoint EndPoint { get; set; } = new(IPAddress.Loopback, 1445);

    /// <summary>
    /// The one account clients sign in with (NTLMv2); null or empty lets every client in as a guest, which Windows refuses
    /// unless its EnableInsecureGuestLogons policy is on, since a guest session cannot be signed.
    /// </summary>
    public string? User { get; set; }

    /// <summary>The account's password; required with <see cref="User"/>.</summary>
    public string? Password { get; set; }

    /// <summary>The server's NetBIOS name in the sign-in challenge.</summary>
    public string ServerName { get; set; } = "SMBSERVER";

    /// <summary>The domain (or workgroup) in the sign-in challenge.</summary>
    public string DomainName { get; set; } = "WORKGROUP";

    /// <summary>Where the server logs: connections at debug, requests at trace, failures as warnings.</summary>
    public ILogger Logger { get; set; } = NullLogger.Instance;
}

/// <summary>A share: its name and the file system behind it.</summary>
public sealed class SmbShare
{
    /// <summary>A share named <paramref name="name"/> serving <paramref name="fileSystem"/>, refusing every change when <paramref name="readOnly"/>.</summary>
    public SmbShare(string name, ISmbFileSystem fileSystem, bool readOnly = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(fileSystem);
        if (name.Equals("IPC$", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("the share name IPC$ is reserved", nameof(name));
        }

        Name = name;
        FileSystem = fileSystem;
        ReadOnly = readOnly;
    }

    /// <summary>The name clients connect to (<c>\\host\name</c>); matched without regard to case.</summary>
    public string Name { get; }

    /// <summary>What the share serves.</summary>
    public ISmbFileSystem FileSystem { get; }

    /// <summary>Whether every change is refused with STATUS_MEDIA_WRITE_PROTECTED.</summary>
    public bool ReadOnly { get; }
}

/// <summary>
/// An SMB 2/3 file server ([MS-SMB2] dialects 2.0.2 to 3.0.2) serving shares from <see cref="ISmbFileSystem"/>s: NTLMv2
/// sign-in through SPNEGO for one account, or guests; message signing; compounded requests; Apple's SMB2 extensions for
/// macOS. Shares can be added and removed while it runs. It listens from <see cref="Start"/> until it is disposed.
/// </summary>
public sealed class SmbServer : IAsyncDisposable
{
    private readonly Socket _listener;
    private readonly ConcurrentDictionary<string, SmbShare> _shares = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<SmbConnection, byte> _connections = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accepting;
    private long _sessions;
    private int _trees;
    private int _disposed;

    private SmbServer(SmbServerOptions options, Socket listener)
    {
        Options = options;
        _listener = listener;
        LocalEndPoint = (IPEndPoint)listener.LocalEndPoint!;
        RandomNumberGenerator.Fill(ServerGuid);
        _accepting = Task.Run(AcceptAsync);
    }

    /// <summary>Raised when the server stops accepting connections on its own (the listening socket failed).</summary>
    public event Action<Exception>? Faulted;

    /// <summary>The address and port the server listens on.</summary>
    public IPEndPoint LocalEndPoint { get; }

    /// <summary>The shares served now.</summary>
    public IReadOnlyCollection<SmbShare> Shares => _shares.Values.ToList();

    internal SmbServerOptions Options { get; }

    /// <summary>The connections being served.</summary>
    internal ConcurrentDictionary<SmbConnection, byte> Connections => _connections;

    internal byte[] ServerGuid { get; } = new byte[16];

    internal ILogger Logger => Options.Logger;

    internal bool GuestOnly => string.IsNullOrEmpty(Options.User);

    /// <summary>Whether the hint about Windows refusing guest sessions was logged already (it is logged once).</summary>
    internal bool WarnedWindowsGuest { get; set; }

    /// <summary>Starts listening on <see cref="SmbServerOptions.EndPoint"/> with the given shares.</summary>
    public static SmbServer Start(SmbServerOptions options, IEnumerable<SmbShare>? shares = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!string.IsNullOrEmpty(options.User) && string.IsNullOrEmpty(options.Password))
        {
            throw new ArgumentException("a password is required with a user", nameof(options));
        }

        var listener = new Socket(options.EndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            if (options.EndPoint.AddressFamily == AddressFamily.InterNetworkV6 && options.EndPoint.Address.Equals(IPAddress.IPv6Any))
            {
                listener.DualMode = true;
            }

            if (!OperatingSystem.IsWindows())
            {
                // a restart can take the port back while the last run's connections are in TIME_WAIT (on Windows the same
                // option would let another process take a port this one holds, so it is left off there)
                listener.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            }

            listener.Bind(options.EndPoint);
            listener.Listen(128);
        }
        catch
        {
            listener.Dispose();
            throw;
        }

        var server = new SmbServer(options, listener);
        foreach (var share in shares ?? [])
        {
            server.AddShare(share);
        }

        return server;
    }

    /// <summary>Starts serving a share; a name already served (in any case) is refused.</summary>
    public void AddShare(SmbShare share)
    {
        ArgumentNullException.ThrowIfNull(share);
        if (!_shares.TryAdd(share.Name, share))
        {
            throw new InvalidOperationException($"a share named '{share.Name}' is already served");
        }

        Logger.LogDebug("smb: serving share {Share}{ReadOnly}", share.Name, share.ReadOnly ? " (read-only)" : "");
    }

    /// <summary>
    /// Stops serving a share: its tree connects answer STATUS_NETWORK_NAME_DELETED from now on and its open files are
    /// closed. False when no share has that name.
    /// </summary>
    public async ValueTask<bool> RemoveShareAsync(string name)
    {
        if (!_shares.TryRemove(name, out var share))
        {
            return false;
        }

        foreach (var connection in _connections.Keys)
        {
            await connection.ForgetShareAsync(share).ConfigureAwait(false);
        }

        Logger.LogDebug("smb: stopped serving share {Share}", share.Name);
        return true;
    }

    /// <summary>Stops listening, drops every connection and closes their files.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Dispose();
        try
        {
            await _accepting.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }

        await Task.WhenAll(_connections.Keys.Select(c => c.CloseAsync())).ConfigureAwait(false);
        _stop.Dispose();
    }

    internal SmbShare? FindShare(string name) => _shares.TryGetValue(name, out var share) ? share : null;

    internal bool Serves(SmbShare share) => _shares.TryGetValue(share.Name, out var current) && ReferenceEquals(current, share);

    internal ulong NextSessionId() => (ulong)Interlocked.Increment(ref _sessions);

    internal uint NextTreeId() => (uint)Interlocked.Increment(ref _trees);

    private async Task AcceptAsync()
    {
        var delay = TimeSpan.Zero;
        while (!_stop.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await _listener.AcceptAsync(_stop.Token).ConfigureAwait(false);
                delay = TimeSpan.Zero;
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.TooManyOpenSockets or SocketError.NoBufferSpaceAvailable)
            {
                // a transient failure (a client gone before it was accepted, descriptors running out) must not end the
                // server: back off and accept again
                delay = delay == TimeSpan.Zero ? TimeSpan.FromMilliseconds(5) : TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, TimeSpan.TicksPerSecond));
                Logger.LogWarning("smb: accepting a connection failed, trying again in {Delay} ms: {Error}", delay.TotalMilliseconds, ex.Message);
                await Task.Delay(delay, _stop.Token).ConfigureAwait(false);
                continue;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "smb: the listener on {EndPoint} failed; no more connections are accepted", LocalEndPoint);
                Faulted?.Invoke(ex);
                return;
            }

            var connection = new SmbConnection(this, socket);
            _connections[connection] = 0;
            _ = Task.Run(async () =>
            {
                try
                {
                    await connection.RunAsync(_stop.Token).ConfigureAwait(false);
                }
                finally
                {
                    _connections.TryRemove(connection, out _);
                }
            });
        }
    }
}
