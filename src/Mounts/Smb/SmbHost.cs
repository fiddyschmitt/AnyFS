using System.Net;
using AnyFS.Sdk;
using LibSmb;
using Microsoft.Extensions.Logging;

namespace AnyFS.Mounts.Smb;

/// <summary>The login every share on one server uses (the server has one user, or guest).</summary>
internal sealed record SmbCredentials(string? User, string? Password, bool Guest)
{
    public bool SameAs(SmbCredentials other) => Guest == other.Guest && (Guest || (string.Equals(User, other.User, StringComparison.Ordinal) && string.Equals(Password, other.Password, StringComparison.Ordinal)));

    public string Describe() => Guest ? "guest access" : $"user '{User}'";
}

/// <summary>
/// One LibSmb server for every collection mounted on the same bind address and port (spec 8.2), in this process: a
/// collection's share comes and goes with its mount while the others carry on. The server listens from the first share
/// to the last, with the login of the first: every collection on the port uses the same one.
/// </summary>
internal sealed class SmbHost(IPAddress address, int port, ILogger logger) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SmbServer? _server;
    private SmbCredentials? _credentials;

    /// <summary>Raised when the server stops accepting connections on its own (the message says why), so the mounts can report it.</summary>
    public event Action<string>? Died;

    public int ShareCount => _server?.Shares.Count ?? 0;

    /// <summary>Whether the server is listening (from the first share to the last).</summary>
    public bool IsRunning => _server is not null;

    /// <summary>Where it listens; null while it does not.</summary>
    public IPEndPoint? EndPoint => _server?.LocalEndPoint;

    public static Task<SmbHost> StartAsync(IPAddress address, int port, ILogger logger, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new SmbHost(address, port, logger));
    }

    /// <summary>Serves a collection as a share named after it; the server starts listening with the first.</summary>
    public async Task RegisterAsync(string collection, IVirtualFileSystem fs, IMountStatsSink stats, SmbCredentials credentials, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_credentials is not null && !_credentials.SameAs(credentials))
            {
                throw new InvalidOperationException($"the SMB server on {address}:{port} already runs with {_credentials.Describe()}; use the same login for every collection on this port, or another port");
            }

            if (_server?.Shares.Any(s => s.Name.Equals(collection, StringComparison.OrdinalIgnoreCase)) == true)
            {
                throw new InvalidOperationException($"'{collection}' is already served on port {port} by another mount");
            }

            if (_server is null)
            {
                try
                {
                    _server = SmbServer.Start(new SmbServerOptions
                    {
                        EndPoint = new IPEndPoint(address, port),
                        User = credentials.Guest ? null : credentials.User,
                        Password = credentials.Guest ? null : credentials.Password,
                        Logger = logger,
                    });
                }
                catch (System.Net.Sockets.SocketException ex)
                {
                    throw new InvalidOperationException($"the SMB server cannot listen on {address}:{port}: {ex.Message}", ex);
                }

                _server.Faulted += OnFaulted;
                _credentials = credentials;
                logger.LogInformation("SMB server listening on {Address}:{Port}", address, port);
            }

            try
            {
                _server.AddShare(new SmbShare(collection, new VfsShareFileSystem(fs, stats), fs.ReadOnly));
            }
            catch when (_server.Shares.Count == 0)
            {
                await StopAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Stops serving a collection; the server stops listening with the last.</summary>
    public async Task UnregisterAsync(string collection)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_server is null || !await _server.RemoveShareAsync(collection).ConfigureAwait(false))
            {
                return;
            }

            if (_server.Shares.Count == 0)
            {
                await StopAsync().ConfigureAwait(false);
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
            await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopAsync()
    {
        var server = _server;
        _server = null;
        _credentials = null;
        if (server is null)
        {
            return;
        }

        server.Faulted -= OnFaulted;
        await server.DisposeAsync().ConfigureAwait(false);
        logger.LogInformation("SMB server on {Address}:{Port} stopped", address, port);
    }

    private void OnFaulted(Exception ex)
    {
        var message = $"the SMB server on {address}:{port} stopped accepting connections: {ex.Message}";
        logger.LogError("{Message}", message);
        Died?.Invoke(message);
    }
}
