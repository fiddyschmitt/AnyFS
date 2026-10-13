using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using AnyFS.Sdk;
using FubarDev.FtpServer;
using FubarDev.FtpServer.AccountManagement;
using FubarDev.FtpServer.FileSystem;
using FubarDev.FtpServer.Localization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NGettext;

namespace AnyFS.Mounts.Ftp;

internal sealed record FtpPassiveOptions(int? MinPort, int? MaxPort, IPAddress? PublicAddress);

/// <summary>
/// One FubarDev FTP server per bind address and port (spec 8.5), shared by every collection mounted there. A login
/// sees the collections it is entitled to: a named user those configured with that user, anonymous those that allow it.
/// </summary>
internal sealed class FtpHost : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly IFtpServerHost _host;
    private readonly IFtpServer _server;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, FtpCollection> _collections = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _statsTimer;
    private long _lastConnections;

    private FtpHost(IPAddress address, int port, FtpPassiveOptions passive, ILogger logger)
    {
        Address = address;
        Port = port;
        _logger = logger;
        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.ClearProviders();
            logging.SetMinimumLevel(LogLevel.Warning);
            logging.AddProvider(new ForwardingLoggerProvider(logger));
        });
        services.Configure<FtpServerOptions>(options =>
        {
            options.ServerAddress = address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) ? "*" : address.ToString();
            options.Port = port;
        });
        services.Configure<SimplePasvOptions>(options =>
        {
            options.PasvMinPort = passive.MinPort;
            options.PasvMaxPort = passive.MaxPort;
            options.PublicAddress = passive.PublicAddress;
        });
        services.AddFtpServer(builder =>
        {
            builder.Services.AddSingleton<IFileSystemClassFactory>(new Factory(this));
            builder.Services.AddSingleton<IMembershipProvider>(new Membership(this));
        });
        // registered last so it wins: the library's loader creates named cultures, which throws under invariant globalization
        services.AddSingleton<IFtpCatalogLoader>(new InvariantCatalogLoader());
        _services = services.BuildServiceProvider();
        _host = _services.GetRequiredService<IFtpServerHost>();
        _server = _services.GetRequiredService<IFtpServer>();
        _statsTimer = new Timer(_ => SyncStats(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public IPAddress Address { get; }

    public int Port { get; }

    public int CollectionCount => _collections.Count;

    public static async Task<FtpHost> StartAsync(IPAddress address, int port, FtpPassiveOptions passive, ILogger logger, CancellationToken ct)
    {
        // FubarDev binds on a background task and reports a busy port only in its log, so check first
        try
        {
            var probe = new TcpListener(address, port);
            probe.Start();
            probe.Stop();
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException($"could not listen on {address}:{port}: {ex.Message}", ex);
        }

        var host = new FtpHost(address, port, passive, logger);
        try
        {
            await host._host.StartAsync(ct).ConfigureAwait(false);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!host._server.Ready && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50, ct).ConfigureAwait(false);
            }

            if (!host._server.Ready)
            {
                throw new InvalidOperationException($"the FTP server on {address}:{port} did not become ready");
            }
        }
        catch
        {
            await host.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        host._statsTimer.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        logger.LogInformation("FTP server listening on {Address}:{Port}", address, port);
        return host;
    }

    public void Register(FtpCollection collection)
    {
        if (!_collections.TryAdd(collection.Name, collection))
        {
            throw new InvalidOperationException($"'{collection.Name}' is already served on port {Port} by another mount");
        }
    }

    public void Unregister(string collectionName) => _collections.TryRemove(collectionName, out _);

    public async ValueTask DisposeAsync()
    {
        await _statsTimer.DisposeAsync().ConfigureAwait(false);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _host.StopAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or ObjectDisposedException)
        {
            _logger.LogDebug(ex, "FTP server on {Address}:{Port}: stop did not finish cleanly", Address, Port);
        }

        _services.Dispose();
        _logger.LogInformation("FTP server on {Address}:{Port} stopped", Address, Port);
    }

    /// <summary>
    /// The library counts connections for the whole server; every collection served here reports the same number
    /// (a session isn't tied to one collection until it changes directory).
    /// </summary>
    private void SyncStats()
    {
        long current;
        try
        {
            current = _server.Statistics.ActiveConnections;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        var delta = current - Interlocked.Exchange(ref _lastConnections, current);
        if (delta == 0)
        {
            return;
        }

        foreach (var collection in _collections.Values)
        {
            for (var i = 0; i < Math.Abs(delta); i++)
            {
                if (delta > 0)
                {
                    collection.Stats.ConnectionOpened();
                }
                else
                {
                    collection.Stats.ConnectionClosed();
                }
            }
        }
    }

    /// <summary>Our own claim on a named login's principal; the anonymous principal (the library's) has none.</summary>
    private const string LoginClaim = "anyfs:login";

    private IReadOnlyList<FtpCollection> VisibleTo(ClaimsPrincipal? user)
    {
        var all = _collections.Values.ToList();
        var login = user?.FindFirst(LoginClaim)?.Value;
        if (login is null)
        {
            return all.Where(c => c.AllowAnonymous).ToList();
        }

        return all.Where(c => c.User is not null && c.User.Equals(login, StringComparison.Ordinal)).ToList();
    }

    private sealed class Factory(FtpHost host) : IFileSystemClassFactory
    {
        public Task<IUnixFileSystem> Create(IAccountInformation accountInformation)
        {
            var visible = host.VisibleTo(accountInformation.FtpUser);
            host._logger.LogDebug("ftp login {Name} ({Claims}) sees {Collections}", accountInformation.FtpUser?.Identity?.Name, string.Join("; ", accountInformation.FtpUser?.Claims.Select(c => c.Type + "=" + c.Value) ?? []), string.Join(", ", visible.Select(c => c.Name)));
            return Task.FromResult<IUnixFileSystem>(new CompositeFtpFileSystem(visible));
        }
    }

    /// <summary>A login is valid when some collection here is configured for it; anonymous when some collection allows it.</summary>
    private sealed class Membership(FtpHost host) : IMembershipProvider, IMembershipProviderAsync
    {
        public Task<MemberValidationResult> ValidateUserAsync(string username, string password) => ValidateUserAsync(username, password, CancellationToken.None);

        public Task<MemberValidationResult> ValidateUserAsync(string username, string password, CancellationToken cancellationToken)
        {
            var collections = host._collections.Values.ToList();
            if (username.Equals("anonymous", StringComparison.OrdinalIgnoreCase) || username.Equals("ftp", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(collections.Any(c => c.AllowAnonymous)
                    ? new MemberValidationResult(MemberValidationStatus.Anonymous, AnonymousMembershipProvider.CreateAnonymousPrincipal(password))
                    : new MemberValidationResult(MemberValidationStatus.InvalidLogin));
            }

            var match = collections.FirstOrDefault(c => c.User is not null && c.User.Equals(username, StringComparison.Ordinal) && string.Equals(c.Password ?? "", password, StringComparison.Ordinal));
            if (match is null)
            {
                return Task.FromResult(new MemberValidationResult(MemberValidationStatus.InvalidLogin));
            }

            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, username), new Claim(LoginClaim, username)], "anyfs");
            return Task.FromResult(new MemberValidationResult(MemberValidationStatus.AuthenticatedUser, new ClaimsPrincipal(identity)));
        }

        public Task LogOutAsync(ClaimsPrincipal principal, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>English replies, no translation files, no named cultures (anyfs runs with invariant globalization).</summary>
    private sealed class InvariantCatalogLoader : IFtpCatalogLoader
    {
        public ICatalog DefaultCatalog { get; } = new Catalog(CultureInfo.InvariantCulture);

        public CultureInfo DefaultLanguage => CultureInfo.InvariantCulture;

        public IReadOnlyCollection<string> GetSupportedLanguages() => ["en"];

        public Task<ICatalog> LoadAsync(CultureInfo language, CancellationToken cancellationToken) => Task.FromResult(DefaultCatalog);
    }

    private sealed class ForwardingLoggerProvider(ILogger target) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => target;

        public void Dispose()
        {
        }
    }
}
