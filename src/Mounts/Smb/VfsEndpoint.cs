using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AnyFS.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AnyFS.Mounts.Smb;

/// <summary>
/// The "bring your own VFS" HTTP service portable-smb-server (and, in M7, portable-nfs-server) talks to (spec 8.2):
/// GET /capabilities /stat /list /read /statfs, POST /create /mkdir /rename /truncate /chtimes, PUT /write,
/// DELETE /remove, every collection under /&lt;collection&gt;, on a loopback port chosen by the OS, with a per-launch
/// random token that every request must carry in X-Vfs-Token. The AnyFS additions to the contract: the token and a
/// per-file <c>readOnly</c> in stat and list answers, which the server turns into FILE_ATTRIBUTE_READONLY and
/// STATUS_ACCESS_DENIED without ever calling a write endpoint.
/// </summary>
internal sealed class VfsEndpoint : IAsyncDisposable
{
    public const string TokenHeader = "X-Vfs-Token";
    private const int MaxChunk = 8 << 20;
    private readonly WebApplication _app;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, Registration> _collections = new(StringComparer.OrdinalIgnoreCase);
    private readonly ReadHandles _reads;

    private VfsEndpoint(ILogger logger)
    {
        _logger = logger;
        _reads = new ReadHandles(TimeSpan.FromSeconds(15), logger);
        Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ApplicationName = "AnyFS",
            EnvironmentName = "Production",
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Logging.AddProvider(new ForwardingLoggerProvider(logger));
        builder.WebHost.UseSetting(WebHostDefaults.SuppressStatusMessagesKey, "true");
        builder.Services.Replace(ServiceDescriptor.Singleton<IHostLifetime, SilentLifetime>());
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = MaxChunk + 1024;
            kestrel.Listen(IPAddress.Loopback, 0);
        });
        _app = builder.Build();
        _app.Run(HandleAsync);
    }

    /// <summary>The secret the server must present; different on every start.</summary>
    public string Token { get; }

    public int Port { get; private set; }

    public int CollectionCount => _collections.Count;

    public static async Task<VfsEndpoint> StartAsync(ILogger logger, CancellationToken ct)
    {
        var endpoint = new VfsEndpoint(logger);
        try
        {
            await endpoint._app.StartAsync(ct).ConfigureAwait(false);
            var address = endpoint._app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
                ?? throw new InvalidOperationException("Kestrel reported no address for the VFS endpoint");
            endpoint.Port = new Uri(address).Port;
        }
        catch
        {
            await endpoint._app.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        logger.LogDebug("VFS endpoint listening on 127.0.0.1:{Port}", endpoint.Port);
        return endpoint;
    }

    /// <summary>The base URL the server is pointed at for a collection (<c>-vfs</c>).</summary>
    public string BaseUrlFor(string collection) => $"http://127.0.0.1:{Port}/{Uri.EscapeDataString(collection)}";

    public void Register(string collection, IVirtualFileSystem fs, IMountStatsSink stats)
    {
        if (!_collections.TryAdd(collection, new Registration(collection, fs, stats)))
        {
            throw new InvalidOperationException($"'{collection}' is already served by this SMB server");
        }
    }

    public void Unregister(string collection) => _collections.TryRemove(collection, out _);

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _app.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            await _app.DisposeAsync().ConfigureAwait(false);
            await _reads.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task HandleAsync(HttpContext ctx)
    {
        var ct = ctx.RequestAborted;
        if (!string.Equals(ctx.Request.Headers[TokenHeader].ToString(), Token, StringComparison.Ordinal))
        {
            await ErrorAsync(ctx, StatusCodes.Status401Unauthorized, "missing or wrong " + TokenHeader, ct).ConfigureAwait(false);
            return;
        }

        var segments = (ctx.Request.Path.Value ?? "/").Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2 || !_collections.TryGetValue(Uri.UnescapeDataString(segments[0]), out var registration))
        {
            await ErrorAsync(ctx, StatusCodes.Status404NotFound, "no such collection or endpoint", ct).ConfigureAwait(false);
            return;
        }

        var endpoint = segments[1];
        var method = ctx.Request.Method;
        var fs = registration.Fs;
        registration.Stats.ConnectionOpened();
        try
        {
            if (endpoint == "capabilities" && HttpMethods.IsGet(method))
            {
                await JsonAsync(ctx, new Capabilities(registration.Name, fs.ReadOnly, false), VfsJson.Default.Capabilities, ct).ConfigureAwait(false);
                return;
            }

            if (endpoint == "statfs" && HttpMethods.IsGet(method))
            {
                var space = await fs.GetSpaceAsync("", ct).ConfigureAwait(false);
                if (space is null)
                {
                    await ErrorAsync(ctx, StatusCodes.Status404NotFound, "no volume information", ct).ConfigureAwait(false);
                    return;
                }

                await JsonAsync(ctx, new Statfs(space.TotalBytes, space.FreeBytes), VfsJson.Default.Statfs, ct).ConfigureAwait(false);
                return;
            }

            if (endpoint == "rename" && HttpMethods.IsPost(method))
            {
                var request = await JsonSerializer.DeserializeAsync(ctx.Request.Body, VfsJson.Default.RenameRequest, ct).ConfigureAwait(false);
                if (request is null || !VfsPath.TryNormalize(request.From, out var from, out var reason) || !VfsPath.TryNormalize(request.To, out var to, out reason))
                {
                    await ErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad rename request", ct).ConfigureAwait(false);
                    return;
                }

                _reads.Invalidate(fs, from);
                _reads.Invalidate(fs, to);
                await fs.RenameAsync(from, to, request.Replace, ct).ConfigureAwait(false);
                await OkAsync(ctx, ct).ConfigureAwait(false);
                return;
            }

            if (!VfsPath.TryNormalize(ctx.Request.Query["path"].ToString(), out var path, out var why))
            {
                await ErrorAsync(ctx, StatusCodes.Status400BadRequest, why, ct).ConfigureAwait(false);
                return;
            }

            switch (endpoint, method)
            {
                case ("stat", "GET"):
                {
                    var entry = await fs.StatAsync(path, ct).ConfigureAwait(false);
                    if (entry is null)
                    {
                        await ErrorAsync(ctx, StatusCodes.Status404NotFound, "not found", ct).ConfigureAwait(false);
                        return;
                    }

                    await JsonAsync(ctx, Info(fs, path, entry), VfsJson.Default.FileInfo, ct).ConfigureAwait(false);
                    return;
                }

                case ("list", "GET"):
                {
                    var entries = new List<FileInfo>();
                    await foreach (var child in fs.ListAsync(path, ct).ConfigureAwait(false))
                    {
                        entries.Add(Info(fs, VfsPath.Join(path, child.Name), child));
                    }

                    await JsonAsync(ctx, new DirEntries(entries), VfsJson.Default.DirEntries, ct).ConfigureAwait(false);
                    return;
                }

                case ("read", "GET"):
                {
                    if (!long.TryParse(ctx.Request.Query["offset"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset) || offset < 0
                        || !int.TryParse(ctx.Request.Query["length"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var length) || length < 0)
                    {
                        await ErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad offset/length", ct).ConfigureAwait(false);
                        return;
                    }

                    length = Math.Min(length, MaxChunk);
                    // the handle outlives this request (ReadHandles): the server reads a file in many pieces
                    using var lease = await _reads.LeaseAsync(fs, path, ct).ConfigureAwait(false);
                    var buffer = ArrayPool<byte>.Shared.Rent(length);
                    try
                    {
                        var n = length == 0 ? 0 : await lease.File.ReadFullyAsync(offset, buffer.AsMemory(0, length), ct).ConfigureAwait(false);
                        ctx.Response.ContentType = "application/octet-stream";
                        ctx.Response.ContentLength = n;
                        if (n > 0)
                        {
                            await ctx.Response.Body.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }

                    return;
                }

                case ("create", "POST"):
                {
                    var exclusive = ctx.Request.Query["exclusive"].ToString().Equals("true", StringComparison.OrdinalIgnoreCase);
                    _reads.Invalidate(fs, path);
                    await using (await fs.OpenAsync(path, exclusive ? VfsOpenMode.CreateNew : VfsOpenMode.Create, ct).ConfigureAwait(false))
                    {
                    }

                    await OkAsync(ctx, ct).ConfigureAwait(false);
                    return;
                }

                case ("write", "PUT"):
                {
                    if (!long.TryParse(ctx.Request.Query["offset"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset) || offset < 0)
                    {
                        await ErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad offset", ct).ConfigureAwait(false);
                        return;
                    }

                    using var body = new MemoryStream();
                    await ctx.Request.Body.CopyToAsync(body, ct).ConfigureAwait(false);
                    if (body.Length > MaxChunk)
                    {
                        await ErrorAsync(ctx, StatusCodes.Status400BadRequest, "write body too large", ct).ConfigureAwait(false);
                        return;
                    }

                    _reads.Invalidate(fs, path);
                    await using (var file = await fs.OpenAsync(path, VfsOpenMode.ReadWrite, ct).ConfigureAwait(false))
                    {
                        await file.WriteAsync(offset, body.GetBuffer().AsMemory(0, (int)body.Length), ct).ConfigureAwait(false);
                        await file.FlushAsync(ct).ConfigureAwait(false);
                    }

                    await JsonAsync(ctx, new WriteResult((int)body.Length), VfsJson.Default.WriteResult, ct).ConfigureAwait(false);
                    return;
                }

                case ("mkdir", "POST"):
                    await fs.CreateDirectoryAsync(path, ct).ConfigureAwait(false);
                    await OkAsync(ctx, ct).ConfigureAwait(false);
                    return;

                case ("remove", "DELETE"):
                    _reads.Invalidate(fs, path);
                    await fs.DeleteAsync(path, ct).ConfigureAwait(false);
                    await OkAsync(ctx, ct).ConfigureAwait(false);
                    return;

                case ("truncate", "POST"):
                {
                    if (!long.TryParse(ctx.Request.Query["size"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) || size < 0)
                    {
                        await ErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad size", ct).ConfigureAwait(false);
                        return;
                    }

                    _reads.Invalidate(fs, path);
                    await using (var file = await fs.OpenAsync(path, VfsOpenMode.ReadWrite, ct).ConfigureAwait(false))
                    {
                        await file.SetLengthAsync(size, ct).ConfigureAwait(false);
                        await file.FlushAsync(ct).ConfigureAwait(false);
                    }

                    await OkAsync(ctx, ct).ConfigureAwait(false);
                    return;
                }

                case ("chtimes", "POST"):
                {
                    if (!DateTimeOffset.TryParse(ctx.Request.Query["modTime"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var modified))
                    {
                        await ErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad modTime", ct).ConfigureAwait(false);
                        return;
                    }

                    await fs.SetTimesAsync(path, null, modified, ct).ConfigureAwait(false);
                    await OkAsync(ctx, ct).ConfigureAwait(false);
                    return;
                }

                default:
                    await ErrorAsync(ctx, StatusCodes.Status404NotFound, "no such endpoint", ct).ConfigureAwait(false);
                    return;
            }
        }
        catch (VfsException ex) when (!ctx.Response.HasStarted)
        {
            var status = StatusFor(ex.Error);
            if (status >= 500)
            {
                _logger.LogWarning("VFS {Method} /{Collection}/{Endpoint}: {Error}", method, registration.Name, endpoint, ex.Message);
            }

            await ErrorAsync(ctx, status, ex.Message, ct).ConfigureAwait(false);
        }
        catch (JsonException ex) when (!ctx.Response.HasStarted)
        {
            await ErrorAsync(ctx, StatusCodes.Status400BadRequest, "bad JSON: " + ex.Message, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            registration.Stats.ConnectionClosed();
        }
    }

    /// <summary>A file is readOnly when the three layers refuse writes to it; directories carry the flag too but the server ignores it for them.</summary>
    private static FileInfo Info(IVirtualFileSystem fs, string path, VfsEntry entry)
        => new(entry.Name, entry.IsDirectory ? 0 : entry.Length, entry.Modified ?? DateTimeOffset.UnixEpoch, entry.IsDirectory, !entry.IsDirectory && !fs.CanWrite(path));

    private static int StatusFor(VfsError error) => error switch
    {
        VfsError.NotFound => StatusCodes.Status404NotFound,
        VfsError.AlreadyExists or VfsError.DirectoryNotEmpty => StatusCodes.Status409Conflict,
        VfsError.ReadOnly or VfsError.AccessDenied => StatusCodes.Status403Forbidden,
        VfsError.InvalidPath or VfsError.IsADirectory or VfsError.NotADirectory => StatusCodes.Status400BadRequest,
        VfsError.NotSupported => StatusCodes.Status501NotImplemented,
        VfsError.Unavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError,
    };

    private static Task OkAsync(HttpContext ctx, CancellationToken ct) => JsonAsync(ctx, new Ok(true), VfsJson.Default.Ok, ct);

    private static async Task JsonAsync<T>(HttpContext ctx, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, CancellationToken ct)
    {
        ctx.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(ctx.Response.Body, value, typeInfo, ct).ConfigureAwait(false);
    }

    private static async Task ErrorAsync(HttpContext ctx, int status, string message, CancellationToken ct)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new ErrorBody(message), VfsJson.Default.ErrorBody);
        ctx.Response.ContentLength = bytes.Length;
        await ctx.Response.Body.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    private sealed record Registration(string Name, IVirtualFileSystem Fs, IMountStatsSink Stats);

    private sealed class ForwardingLoggerProvider(ILogger target) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => target;

        public void Dispose()
        {
        }
    }

    private sealed class SilentLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

internal sealed record Capabilities(string Name, bool ReadOnly, bool CaseInsensitive);

internal sealed record FileInfo(string Name, long Size, DateTimeOffset ModTime, bool IsDir, bool ReadOnly);

internal sealed record DirEntries(List<FileInfo> Entries);

internal sealed record Statfs(long TotalBytes, long FreeBytes);

internal sealed record WriteResult(int Written);

internal sealed record RenameRequest(string From, string To, bool Replace);

internal sealed record ErrorBody(string Error);

internal sealed record Ok(bool Success);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(Capabilities))]
[JsonSerializable(typeof(FileInfo))]
[JsonSerializable(typeof(DirEntries))]
[JsonSerializable(typeof(Statfs))]
[JsonSerializable(typeof(WriteResult))]
[JsonSerializable(typeof(RenameRequest))]
[JsonSerializable(typeof(ErrorBody))]
[JsonSerializable(typeof(Ok))]
internal sealed partial class VfsJson : JsonSerializerContext
{
}
