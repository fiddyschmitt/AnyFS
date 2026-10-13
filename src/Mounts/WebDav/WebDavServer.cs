using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using AnyFS.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace AnyFS.Mounts.WebDav;

/// <summary>Optional Basic credentials of one collection's mount.</summary>
internal sealed record DavCredentials(string User, string Password);

/// <summary>
/// The minimal RFC 4918 server of spec 8.4, on Kestrel, one per bind address and port, every collection at
/// /&lt;collection&gt;/. Class 1 plus class 2 (exclusive write locks in memory), PROPFIND depth 0 and 1, GET with ranges,
/// PUT, MKCOL, DELETE, MOVE, COPY, PROPPATCH (accepted and ignored), enough for the Windows redirector, Finder,
/// GNOME/KDE and davfs2. A single request delegate, no routing, no MVC: trim-safe.
/// </summary>
internal sealed partial class WebDavServer : IAsyncDisposable
{
    private const string AllowHeader = "OPTIONS, GET, HEAD, PUT, DELETE, PROPFIND, PROPPATCH, MKCOL, MOVE, COPY, LOCK, UNLOCK";
    private static readonly object SinkKey = new();
    private readonly WebApplication _app;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, Registration> _collections = new(StringComparer.OrdinalIgnoreCase);
    private readonly LockManager _locks = new();

    private WebDavServer(IPAddress address, int port, ILogger logger)
    {
        Address = address;
        Port = port;
        _logger = logger;
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
            kestrel.Limits.MaxRequestBodySize = null;
            kestrel.Listen(address, port, listen => listen.Use(next => async connection =>
            {
                try
                {
                    await next(connection).ConfigureAwait(false);
                }
                finally
                {
                    if (connection.Items.TryGetValue(SinkKey, out var item) && item is IMountStatsSink sink)
                    {
                        sink.ConnectionClosed();
                    }
                }
            }));
        });
        _app = builder.Build();
        _app.Run(HandleAsync);
    }

    public IPAddress Address { get; }

    public int Port { get; }

    public int CollectionCount => _collections.Count;

    internal LockManager Locks => _locks;

    public static async Task<WebDavServer> StartAsync(IPAddress address, int port, ILogger logger, CancellationToken ct)
    {
        var server = new WebDavServer(address, port, logger);
        try
        {
            await server._app.StartAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await server._app.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"could not listen on {address}:{port}: {ex.GetBaseException().Message}", ex);
        }

        logger.LogInformation("WebDAV server listening on {Address}:{Port}", address, port);
        return server;
    }

    public void Register(string collectionName, IVirtualFileSystem fs, IMountStatsSink stats, DavCredentials? credentials)
    {
        if (!_collections.TryAdd(collectionName, new Registration(collectionName, fs, stats, credentials)))
        {
            throw new InvalidOperationException($"'{collectionName}' is already served on port {Port} by another mount");
        }
    }

    public void Unregister(string collectionName)
    {
        _collections.TryRemove(collectionName, out _);
        _locks.ForgetCollection(collectionName);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _app.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            await _app.DisposeAsync().ConfigureAwait(false);
        }

        _logger.LogInformation("WebDAV server on {Address}:{Port} stopped", Address, Port);
    }

    // ----- dispatch -----

    private async Task HandleAsync(HttpContext ctx)
    {
        var ct = ctx.RequestAborted;
        ctx.Response.Headers["DAV"] = "1, 2";
        ctx.Response.Headers["MS-Author-Via"] = "DAV";
        var method = ctx.Request.Method.ToUpperInvariant();
        var path = ctx.Request.Path.Value ?? "/";
        var (collectionName, rest) = SplitPath(path);

        if (collectionName.Length == 0)
        {
            await HandleRootAsync(ctx, method, ct).ConfigureAwait(false);
            return;
        }

        if (!_collections.TryGetValue(collectionName, out var registration))
        {
            await ErrorAsync(ctx, StatusCodes.Status404NotFound, $"There is no collection named '{collectionName}' here.", ct).ConfigureAwait(false);
            return;
        }

        if (!Authorised(ctx, registration))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"AnyFS\", charset=\"UTF-8\"";
            return;
        }

        AttachConnection(ctx, registration);
        if (!VfsPath.TryNormalize(rest, out var vfsPath, out var reason))
        {
            await ErrorAsync(ctx, StatusCodes.Status400BadRequest, reason, ct).ConfigureAwait(false);
            return;
        }

        var request = new DavRequest(ctx, registration, vfsPath, rest.EndsWith('/') || rest.Length == 0);
        try
        {
            switch (method)
            {
                case "OPTIONS":
                    ctx.Response.Headers.Allow = AllowHeader;
                    ctx.Response.ContentLength = 0;
                    break;
                case "PROPFIND":
                    await PropfindAsync(request, ct).ConfigureAwait(false);
                    break;
                case "PROPPATCH":
                    await ProppatchAsync(request, ct).ConfigureAwait(false);
                    break;
                case "GET":
                case "HEAD":
                    await GetAsync(request, ct).ConfigureAwait(false);
                    break;
                case "PUT":
                    await PutAsync(request, ct).ConfigureAwait(false);
                    break;
                case "MKCOL":
                    await MkcolAsync(request, ct).ConfigureAwait(false);
                    break;
                case "DELETE":
                    await DeleteAsync(request, ct).ConfigureAwait(false);
                    break;
                case "MOVE":
                case "COPY":
                    await CopyMoveAsync(request, method == "MOVE", ct).ConfigureAwait(false);
                    break;
                case "LOCK":
                    await LockAsync(request, ct).ConfigureAwait(false);
                    break;
                case "UNLOCK":
                    await UnlockAsync(request, ct).ConfigureAwait(false);
                    break;
                default:
                    ctx.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                    ctx.Response.Headers.Allow = AllowHeader;
                    break;
            }
        }
        catch (VfsException ex) when (!ctx.Response.HasStarted)
        {
            var status = StatusFor(ex.Error);
            if (status >= 500)
            {
                _logger.LogWarning("WebDAV {Method} {Path}: {Error}", method, path, ex.Message);
            }

            await ErrorAsync(ctx, status, ex.Message, ct).ConfigureAwait(false);
        }
        catch (XmlException ex) when (!ctx.Response.HasStarted)
        {
            await ErrorAsync(ctx, StatusCodes.Status400BadRequest, "The request body is not well-formed XML: " + ex.Message, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    /// <summary>The server root: a virtual collection listing the collections served here (no auth, no writes).</summary>
    private async Task HandleRootAsync(HttpContext ctx, string method, CancellationToken ct)
    {
        var names = _collections.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        switch (method)
        {
            case "OPTIONS":
                ctx.Response.Headers.Allow = "OPTIONS, GET, HEAD, PROPFIND";
                ctx.Response.ContentLength = 0;
                return;
            case "PROPFIND":
            {
                var request = DavXml.ParsePropfind(await DavXml.ReadAsync(ctx.Request.Body, ctx.Request.ContentLength, ct).ConfigureAwait(false));
                var now = DateTimeOffset.UtcNow;
                var responses = new List<XElement> { DavXml.ResponseFor("/", VfsEntry.Directory("", now, now), null, null, null, request, writable: false) };
                if (Depth(ctx) != 0)
                {
                    responses.AddRange(names.Select(n => DavXml.ResponseFor(HrefFor(n, "", true), VfsEntry.Directory(n, now, now), null, null, null, request, writable: false)));
                }

                await WriteXmlAsync(ctx, StatusCodes.Status207MultiStatus, DavXml.MultiStatus(responses), ct).ConfigureAwait(false);
                return;
            }
            case "GET":
            case "HEAD":
            {
                var sb = new StringBuilder("<!DOCTYPE html>\n<html><head><meta charset=\"utf-8\"><title>AnyFS WebDAV</title></head><body><h1>AnyFS</h1><ul>\n");
                foreach (var name in names)
                {
                    sb.Append($"<li><a href=\"{HrefFor(name, "", true)}\">{WebUtility.HtmlEncode(name)}/</a></li>\n");
                }

                sb.Append("</ul></body></html>\n");
                await WriteHtmlAsync(ctx, sb.ToString(), ct).ConfigureAwait(false);
                return;
            }
            default:
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
        }
    }

    // ----- methods -----

    private async Task PropfindAsync(DavRequest r, CancellationToken ct)
    {
        var depth = Depth(r.Ctx);
        if (depth < 0)
        {
            // RFC 4918 9.1: a server may refuse infinite depth; the Windows redirector, Finder and davfs2 never ask for it
            await WriteXmlAsync(r.Ctx, StatusCodes.Status403Forbidden, DavXml.Error(DavXml.D + "propfind-finite-depth"), ct).ConfigureAwait(false);
            return;
        }

        var request = DavXml.ParsePropfind(await DavXml.ReadAsync(r.Ctx.Request.Body, r.Ctx.Request.ContentLength, ct).ConfigureAwait(false));
        var entry = await r.Fs.StatAsync(r.Path, ct).ConfigureAwait(false);
        if (entry is null)
        {
            await ErrorAsync(r.Ctx, StatusCodes.Status404NotFound, "Not found.", ct).ConfigureAwait(false);
            return;
        }

        var wantsQuota = request.Names.Contains(DavXml.QuotaAvailable) || request.Names.Contains(DavXml.QuotaUsed);
        var space = wantsQuota ? await SpaceAsync(r.Fs, r.Path, ct).ConfigureAwait(false) : null;
        var responses = new List<XElement> { ResponseFor(r, r.Path, entry, space, request) };
        if (depth > 0 && entry.IsDirectory)
        {
            await foreach (var child in r.Fs.ListAsync(r.Path, ct).ConfigureAwait(false))
            {
                responses.Add(ResponseFor(r, VfsPath.Join(r.Path, child.Name), child, space, request));
            }
        }

        await WriteXmlAsync(r.Ctx, StatusCodes.Status207MultiStatus, DavXml.MultiStatus(responses), ct).ConfigureAwait(false);
    }

    private XElement ResponseFor(DavRequest r, string path, VfsEntry entry, VfsSpace? space, PropfindRequest request)
    {
        var held = _locks.Covering(r.Collection, path);
        var lockRoot = held is null ? null : HrefFor(r.Collection, held.Path, held.Path.Length == 0 || entry.IsDirectory && held.Path == path);
        return DavXml.ResponseFor(HrefFor(r.Collection, path, entry.IsDirectory), entry, held, lockRoot, space, request, r.Fs.CanWrite(path));
    }

    /// <summary>Dead properties are accepted and forgotten; the DAV: live ones are refused with 403 (spec 8.4).</summary>
    private async Task ProppatchAsync(DavRequest r, CancellationToken ct)
    {
        var entry = await r.Fs.StatAsync(r.Path, ct).ConfigureAwait(false);
        if (entry is null)
        {
            await ErrorAsync(r.Ctx, StatusCodes.Status404NotFound, "Not found.", ct).ConfigureAwait(false);
            return;
        }

        if (!await CheckWritableAsync(r, r.Path, ct).ConfigureAwait(false))
        {
            return;
        }

        var updates = DavXml.ParseProppatch(await DavXml.ReadAsync(r.Ctx.Request.Body, r.Ctx.Request.ContentLength, ct).ConfigureAwait(false));
        var accepted = new List<XElement>();
        var refused = new List<XElement>();
        foreach (var (name, _) in updates)
        {
            (DavXml.LiveProps.Contains(name) ? refused : accepted).Add(new XElement(name));
        }

        var propstats = new List<XElement>();
        if (accepted.Count > 0 || refused.Count == 0)
        {
            propstats.Add(DavXml.Propstat(accepted, 200));
        }

        if (refused.Count > 0)
        {
            propstats.Add(DavXml.Propstat(refused, 403));
        }

        await WriteXmlAsync(r.Ctx, StatusCodes.Status207MultiStatus, DavXml.MultiStatus([DavXml.Response(HrefFor(r.Collection, r.Path, entry.IsDirectory), propstats.ToArray())]), ct).ConfigureAwait(false);
    }

    private async Task GetAsync(DavRequest r, CancellationToken ct)
    {
        var entry = await r.Fs.StatAsync(r.Path, ct).ConfigureAwait(false);
        if (entry is null)
        {
            await ErrorAsync(r.Ctx, StatusCodes.Status404NotFound, "Not found.", ct).ConfigureAwait(false);
            return;
        }

        if (entry.IsDirectory)
        {
            var sb = new StringBuilder("<!DOCTYPE html>\n<html><head><meta charset=\"utf-8\"><title>").Append(WebUtility.HtmlEncode("/" + r.Collection + "/" + r.Path)).Append("</title></head><body><h1>");
            sb.Append(WebUtility.HtmlEncode(r.Path.Length == 0 ? r.Collection : VfsPath.Name(r.Path))).Append("</h1><ul>\n");
            if (r.Path.Length > 0)
            {
                sb.Append("<li><a href=\"../\">../</a></li>\n");
            }

            await foreach (var child in r.Fs.ListAsync(r.Path, ct).ConfigureAwait(false))
            {
                var href = Uri.EscapeDataString(child.Name) + (child.IsDirectory ? "/" : "");
                sb.Append($"<li><a href=\"{href}\">{WebUtility.HtmlEncode(child.Name)}{(child.IsDirectory ? "/" : "")}</a></li>\n");
            }

            sb.Append("</ul></body></html>\n");
            await WriteHtmlAsync(r.Ctx, sb.ToString(), ct).ConfigureAwait(false);
            return;
        }

        var file = await r.Fs.OpenAsync(r.Path, VfsOpenMode.Read, ct).ConfigureAwait(false);
        var stream = new VfsFileStream(file, ownsFile: true);
        var etag = new EntityTagHeaderValue(DavXml.ETagFor(entry));
        var result = Results.File(stream, DavXml.ContentTypeFor(entry.Name), fileDownloadName: null, lastModified: entry.Modified, entityTag: etag, enableRangeProcessing: true);
        await result.ExecuteAsync(r.Ctx).ConfigureAwait(false);
    }

    private async Task PutAsync(DavRequest r, CancellationToken ct)
    {
        if (r.Path.Length == 0)
        {
            await ErrorAsync(r.Ctx, StatusCodes.Status405MethodNotAllowed, "The collection root is a folder.", ct).ConfigureAwait(false);
            return;
        }

        if (r.Ctx.Request.Headers.ContainsKey("Content-Range"))
        {
            await ErrorAsync(r.Ctx, StatusCodes.Status400BadRequest, "Partial PUT (Content-Range) is not supported.", ct).ConfigureAwait(false);
            return;
        }

        var existing = await r.Fs.StatAsync(r.Path, ct).ConfigureAwait(false);
        if (existing is { IsDirectory: true })
        {
            await ErrorAsync(r.Ctx, StatusCodes.Status405MethodNotAllowed, "That path is a folder.", ct).ConfigureAwait(false);
            return;
        }

        if (!await CheckWritableAsync(r, r.Path, ct).ConfigureAwait(false))
        {
            return;
        }

        var parent = await r.Fs.StatAsync(VfsPath.Parent(r.Path), ct).ConfigureAwait(false);
        if (parent is not { IsDirectory: true })
        {
            await ErrorAsync(r.Ctx, StatusCodes.Status409Conflict, "The parent folder does not exist.", ct).ConfigureAwait(false);
            return;
        }

        await using (var file = await r.Fs.OpenAsync(r.Path, VfsOpenMode.Create, ct).ConfigureAwait(false))
        {
            var buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
            try
            {
                long offset = 0;
                int n;
                while ((n = await r.Ctx.Request.Body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(offset, buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    offset += n;
                }

                await file.FlushAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        r.Ctx.Response.StatusCode = existing is null ? StatusCodes.Status201Created : StatusCodes.Status204NoContent;
        r.Ctx.Response.ContentLength = 0;
    }

    private async Task MkcolAsync(DavRequest r, CancellationToken ct)
    {
        if (r.Ctx.Request.ContentLength > 0)
        {
            await ErrorAsync(r.Ctx, StatusCodes.Status415UnsupportedMediaType, "MKCOL with a body is not supported.", ct).ConfigureAwait(false);
            return;
        }

        if (r.Path.Length == 0 || await r.Fs.StatAsync(r.Path, ct).ConfigureAwait(false) is not null)
        {
            await ErrorAsync(r.Ctx, StatusCodes.Status405MethodNotAllowed, "That path already exists.", ct).ConfigureAwait(false);
            return;
        }

        if (!await CheckWritableAsync(r, r.Path, ct).ConfigureAwait(false))
        {
            return;
        }

        if (await r.Fs.StatAsync(VfsPath.Parent(r.Path), ct).ConfigureAwait(false) is not { IsDirectory: true })
        {
            await ErrorAsync(r.Ctx, StatusCodes.Status409Conflict, "The parent folder does not exist.", ct).ConfigureAwait(false);
            return;
        }

        await r.Fs.CreateDirectoryAsync(r.Path, ct).ConfigureAwait(false);
        r.Ctx.Response.StatusCode = StatusCodes.Status201Created;
        r.Ctx.Response.ContentLength = 0;
    }

    private async Task DeleteAsync(DavRequest r, CancellationToken ct)
    {
        var entry = await r.Fs.StatAsync(r.Path, ct).ConfigureAwait(false);
        if (entry is null)
        {
            await ErrorAsync(r.Ctx, StatusCodes.Status404NotFound, "Not found.", ct).ConfigureAwait(false);
            return;
        }

        if (r.Path.Length == 0)
        {
            await ErrorAsync(r.Ctx, StatusCodes.Status403Forbidden, "The collection root cannot be deleted.", ct).ConfigureAwait(false);
            return;
        }

        if (!await CheckWritableAsync(r, r.Path, ct).ConfigureAwait(false))
        {
            return;
        }

        if (entry.IsDirectory)
        {
            await DeleteTreeAsync(r.Fs, r.Path, ct).ConfigureAwait(false);
        }
        else
        {
            await r.Fs.DeleteAsync(r.Path, ct).ConfigureAwait(false);
        }

        _locks.Forget(r.Collection, r.Path);
        r.Ctx.Response.StatusCode = StatusCodes.Status204NoContent;
        r.Ctx.Response.ContentLength = 0;
    }

    private async Task CopyMoveAsync(DavRequest r, bool move, CancellationToken ct)
    {
        var entry = await r.Fs.StatAsync(r.Path, ct).ConfigureAwait(false);
        if (entry is null)
        {
            await ErrorAsync(r.Ctx, StatusCodes.Status404NotFound, "Not found.", ct).ConfigureAwait(false);
            return;
        }

        var destination = r.Ctx.Request.Headers["Destination"].ToString();
        var destinationPath = Uri.TryCreate(destination, UriKind.Absolute, out var uri) ? uri.AbsolutePath : destination;
        var (destCollection, destRest) = SplitPath(destinationPath);
        if (!destCollection.Equals(r.Collection, StringComparison.OrdinalIgnoreCase) || !VfsPath.TryNormalize(destRest, out var destPath, out _))
        {
            await ErrorAsync(r.Ctx, StatusCodes.Status502BadGateway, "The destination must be inside the same collection.", ct).ConfigureAwait(false);
            return;
        }

        if (r.Path.Length == 0 || destPath.Length == 0 || destPath == r.Path || VfsPath.IsUnder(destPath, r.Path))
        {
            await ErrorAsync(r.Ctx, StatusCodes.Status403Forbidden, "The source and destination overlap.", ct).ConfigureAwait(false);
            return;
        }

        var overwrite = !r.Ctx.Request.Headers["Overwrite"].ToString().Equals("F", StringComparison.OrdinalIgnoreCase);
        var existed = await r.Fs.StatAsync(destPath, ct).ConfigureAwait(false);
        if (existed is not null && !overwrite)
        {
            await ErrorAsync(r.Ctx, StatusCodes.Status412PreconditionFailed, "The destination exists and Overwrite is F.", ct).ConfigureAwait(false);
            return;
        }

        if (move && !await CheckWritableAsync(r, r.Path, ct).ConfigureAwait(false))
        {
            return;
        }

        if (!await CheckWritableAsync(r, destPath, ct).ConfigureAwait(false))
        {
            return;
        }

        if (await r.Fs.StatAsync(VfsPath.Parent(destPath), ct).ConfigureAwait(false) is not { IsDirectory: true })
        {
            await ErrorAsync(r.Ctx, StatusCodes.Status409Conflict, "The destination's parent folder does not exist.", ct).ConfigureAwait(false);
            return;
        }

        if (existed is not null && (existed.IsDirectory || entry.IsDirectory))
        {
            // a folder in the way (or a folder arriving): clear the target first, as RFC 4918 9.8.4 and 9.9.3 want
            if (existed.IsDirectory)
            {
                await DeleteTreeAsync(r.Fs, destPath, ct).ConfigureAwait(false);
            }
            else
            {
                await r.Fs.DeleteAsync(destPath, ct).ConfigureAwait(false);
            }

            existed = null;
        }

        if (move)
        {
            await r.Fs.RenameAsync(r.Path, destPath, replace: true, ct).ConfigureAwait(false);
            _locks.Forget(r.Collection, r.Path);
        }
        else
        {
            var depthInfinity = Depth(r.Ctx) != 0;
            await CopyTreeAsync(r.Fs, r.Path, destPath, entry, depthInfinity, ct).ConfigureAwait(false);
        }

        r.Ctx.Response.StatusCode = existed is null ? StatusCodes.Status201Created : StatusCodes.Status204NoContent;
        r.Ctx.Response.ContentLength = 0;
    }

    private async Task LockAsync(DavRequest r, CancellationToken ct)
    {
        var entry = await r.Fs.StatAsync(r.Path, ct).ConfigureAwait(false);
        if (r.Path.Length == 0 && entry is null)
        {
            await ErrorAsync(r.Ctx, StatusCodes.Status404NotFound, "Not found.", ct).ConfigureAwait(false);
            return;
        }

        var tokens = TokensOf(r.Ctx);
        var timeout = LockManager.ParseTimeout(r.Ctx.Request.Headers["Timeout"].ToString());
        var info = DavXml.ParseLockInfo(await DavXml.ReadAsync(r.Ctx.Request.Body, r.Ctx.Request.ContentLength, ct).ConfigureAwait(false));
        if (info is null)
        {
            // no body: a refresh of the lock named in If
            var held = _locks.Covering(r.Collection, r.Path);
            if (held is null || !tokens.Contains(held.Token))
            {
                await ErrorAsync(r.Ctx, StatusCodes.Status412PreconditionFailed, "No lock to refresh (send the lock token in If).", ct).ConfigureAwait(false);
                return;
            }

            _locks.Lock(r.Collection, held.Path, held.InfiniteDepth, held.OwnerXml, timeout, tokens, out _);
            await WriteLockAsync(r, held, StatusCodes.Status200OK, ct).ConfigureAwait(false);
            return;
        }

        if (!info.Exclusive || !info.Write)
        {
            await ErrorAsync(r.Ctx, StatusCodes.Status400BadRequest, "Only exclusive write locks are supported.", ct).ConfigureAwait(false);
            return;
        }

        if (!r.Fs.CanWrite(r.Path) && (entry is not null || !r.Fs.CanWrite(VfsPath.Parent(r.Path))))
        {
            await ErrorAsync(r.Ctx, StatusCodes.Status403Forbidden, "That path is read-only.", ct).ConfigureAwait(false);
            return;
        }

        var created = false;
        if (entry is null)
        {
            // a lock-null resource: an empty file that the client will PUT into (what Office and the Windows redirector do)
            if (await r.Fs.StatAsync(VfsPath.Parent(r.Path), ct).ConfigureAwait(false) is not { IsDirectory: true })
            {
                await ErrorAsync(r.Ctx, StatusCodes.Status409Conflict, "The parent folder does not exist.", ct).ConfigureAwait(false);
                return;
            }

            await using (await r.Fs.OpenAsync(r.Path, VfsOpenMode.CreateNew, ct).ConfigureAwait(false))
            {
            }

            created = true;
            entry = await r.Fs.StatAsync(r.Path, ct).ConfigureAwait(false);
        }

        var infinite = entry is { IsDirectory: true } && Depth(r.Ctx) != 0;
        var dav = _locks.Lock(r.Collection, r.Path, infinite, info.OwnerXml, timeout, tokens, out var conflict);
        if (dav is null)
        {
            await ErrorAsync(r.Ctx, StatusCodes.Status423Locked, conflict ? "Locked by someone else." : "Could not lock.", ct).ConfigureAwait(false);
            return;
        }

        await WriteLockAsync(r, dav, created ? StatusCodes.Status201Created : StatusCodes.Status200OK, ct).ConfigureAwait(false);
    }

    private async Task UnlockAsync(DavRequest r, CancellationToken ct)
    {
        var token = r.Ctx.Request.Headers["Lock-Token"].ToString().Trim().TrimStart('<').TrimEnd('>');
        if (token.Length == 0)
        {
            await ErrorAsync(r.Ctx, StatusCodes.Status400BadRequest, "Lock-Token is missing.", ct).ConfigureAwait(false);
            return;
        }

        if (!_locks.Unlock(r.Collection, r.Path, token))
        {
            await ErrorAsync(r.Ctx, StatusCodes.Status409Conflict, "No such lock.", ct).ConfigureAwait(false);
            return;
        }

        r.Ctx.Response.StatusCode = StatusCodes.Status204NoContent;
        r.Ctx.Response.ContentLength = 0;
    }

    // ----- helpers -----

    /// <summary>Read-only layers first (403), then the lock (423). False when the response has been written.</summary>
    private async Task<bool> CheckWritableAsync(DavRequest r, string path, CancellationToken ct)
    {
        var exists = await r.Fs.StatAsync(path, ct).ConfigureAwait(false) is not null;
        if (!(exists ? r.Fs.CanWrite(path) : r.Fs.CanWrite(VfsPath.Parent(path))))
        {
            await ErrorAsync(r.Ctx, StatusCodes.Status403Forbidden, r.Fs.ReadOnly ? "This mount is read-only." : "That path is read-only.", ct).ConfigureAwait(false);
            return false;
        }

        if (!_locks.IsAllowed(r.Collection, path, TokensOf(r.Ctx)))
        {
            await ErrorAsync(r.Ctx, StatusCodes.Status423Locked, "The resource is locked.", ct).ConfigureAwait(false);
            return false;
        }

        return true;
    }

    private async Task WriteLockAsync(DavRequest r, DavLock held, int status, CancellationToken ct)
    {
        r.Ctx.Response.Headers["Lock-Token"] = "<" + held.Token + ">";
        var isDir = held.Path.Length == 0 || (await r.Fs.StatAsync(held.Path, ct).ConfigureAwait(false))?.IsDirectory == true;
        await WriteXmlAsync(r.Ctx, status, DavXml.LockDiscoveryProp(held, HrefFor(r.Collection, held.Path, isDir)), ct).ConfigureAwait(false);
    }

    private static async Task DeleteTreeAsync(IVirtualFileSystem fs, string path, CancellationToken ct)
    {
        var children = new List<VfsEntry>();
        await foreach (var child in fs.ListAsync(path, ct).ConfigureAwait(false))
        {
            children.Add(child);
        }

        foreach (var child in children)
        {
            var childPath = VfsPath.Join(path, child.Name);
            if (child.IsDirectory)
            {
                await DeleteTreeAsync(fs, childPath, ct).ConfigureAwait(false);
            }
            else
            {
                await fs.DeleteAsync(childPath, ct).ConfigureAwait(false);
            }
        }

        await fs.DeleteAsync(path, ct).ConfigureAwait(false);
    }

    private static async Task CopyTreeAsync(IVirtualFileSystem fs, string from, string to, VfsEntry entry, bool recurse, CancellationToken ct)
    {
        if (!entry.IsDirectory)
        {
            await CopyFileAsync(fs, from, to, ct).ConfigureAwait(false);
            return;
        }

        await fs.CreateDirectoryAsync(to, ct).ConfigureAwait(false);
        if (!recurse)
        {
            return;
        }

        var children = new List<VfsEntry>();
        await foreach (var child in fs.ListAsync(from, ct).ConfigureAwait(false))
        {
            children.Add(child);
        }

        foreach (var child in children)
        {
            await CopyTreeAsync(fs, VfsPath.Join(from, child.Name), VfsPath.Join(to, child.Name), child, recurse, ct).ConfigureAwait(false);
        }
    }

    private static async Task CopyFileAsync(IVirtualFileSystem fs, string from, string to, CancellationToken ct)
    {
        await using var source = await fs.OpenAsync(from, VfsOpenMode.Read, ct).ConfigureAwait(false);
        await using var target = await fs.OpenAsync(to, VfsOpenMode.Create, ct).ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
        try
        {
            long offset = 0;
            while (true)
            {
                var n = await source.ReadAsync(offset, buffer, ct).ConfigureAwait(false);
                if (n <= 0)
                {
                    break;
                }

                await target.WriteAsync(offset, buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                offset += n;
            }

            await target.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<VfsSpace?> SpaceAsync(IVirtualFileSystem fs, string path, CancellationToken ct)
    {
        try
        {
            return await fs.GetSpaceAsync(path, ct).ConfigureAwait(false);
        }
        catch (VfsException)
        {
            return null;
        }
    }

    private static bool Authorised(HttpContext ctx, Registration registration)
    {
        if (registration.Credentials is not { } expected)
        {
            return true;
        }

        var header = ctx.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
            var colon = decoded.IndexOf(':');
            if (colon < 0)
            {
                return false;
            }

            return string.Equals(decoded[..colon], expected.User, StringComparison.Ordinal) && string.Equals(decoded[(colon + 1)..], expected.Password, StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Depth header: 0, 1, or -1 for infinity. Absent means 1 here (RFC says infinity, which we don't serve).</summary>
    private static int Depth(HttpContext ctx)
    {
        var depth = ctx.Request.Headers["Depth"].ToString().Trim();
        return depth switch
        {
            "0" => 0,
            "1" or "" => 1,
            _ => -1,
        };
    }

    [GeneratedRegex("<(opaquelocktoken:[^>]+)>", RegexOptions.IgnoreCase)]
    private static partial Regex TokenPattern();

    /// <summary>Every lock token in the If header (tagged or not; the Not operator is ignored).</summary>
    private static List<string> TokensOf(HttpContext ctx)
    {
        var header = ctx.Request.Headers["If"].ToString();
        return header.Length == 0 ? [] : TokenPattern().Matches(header).Select(m => m.Groups[1].Value).ToList();
    }

    private static (string Collection, string Remainder) SplitPath(string path)
    {
        var trimmed = Uri.UnescapeDataString(path).Replace('\\', '/').TrimStart('/');
        if (trimmed.Length == 0)
        {
            return ("", "");
        }

        var slash = trimmed.IndexOf('/');
        return slash < 0 ? (trimmed, "") : (trimmed[..slash], trimmed[(slash + 1)..]);
    }

    internal static string HrefFor(string collection, string vfsPath, bool directory)
    {
        var sb = new StringBuilder("/").Append(Uri.EscapeDataString(collection)).Append('/');
        var segments = VfsPath.Split(vfsPath);
        for (var i = 0; i < segments.Length; i++)
        {
            sb.Append(Uri.EscapeDataString(segments[i]));
            if (i < segments.Length - 1 || directory)
            {
                sb.Append('/');
            }
        }

        return sb.ToString();
    }

    private static int StatusFor(VfsError error) => error switch
    {
        VfsError.NotFound => StatusCodes.Status404NotFound,
        VfsError.AlreadyExists => StatusCodes.Status412PreconditionFailed,
        VfsError.NotADirectory or VfsError.DirectoryNotEmpty => StatusCodes.Status409Conflict,
        VfsError.IsADirectory => StatusCodes.Status405MethodNotAllowed,
        VfsError.ReadOnly or VfsError.AccessDenied => StatusCodes.Status403Forbidden,
        VfsError.InvalidPath => StatusCodes.Status400BadRequest,
        VfsError.NotSupported => StatusCodes.Status501NotImplemented,
        VfsError.Unavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError,
    };

    private static void AttachConnection(HttpContext ctx, Registration registration)
    {
        var items = ctx.Features.Get<IConnectionItemsFeature>()?.Items;
        if (items is null || items.ContainsKey(SinkKey))
        {
            return;
        }

        items[SinkKey] = registration.Stats;
        registration.Stats.ConnectionOpened();
    }

    private static async Task WriteXmlAsync(HttpContext ctx, int status, XElement body, CancellationToken ct)
    {
        var bytes = DavXml.ToBytes(body);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "text/xml; charset=\"utf-8\"";
        ctx.Response.ContentLength = bytes.Length;
        await ctx.Response.Body.WriteAsync(bytes, ct).ConfigureAwait(false);
    }

    private static async Task WriteHtmlAsync(HttpContext ctx, string html, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(html);
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.ContentLength = bytes.Length;
        if (!HttpMethods.IsHead(ctx.Request.Method))
        {
            await ctx.Response.Body.WriteAsync(bytes, ct).ConfigureAwait(false);
        }
    }

    private static async Task ErrorAsync(HttpContext ctx, int status, string message, CancellationToken ct)
    {
        ctx.Response.StatusCode = status;
        var bytes = Encoding.UTF8.GetBytes(message + "\n");
        ctx.Response.ContentType = "text/plain; charset=utf-8";
        ctx.Response.ContentLength = bytes.Length;
        if (!HttpMethods.IsHead(ctx.Request.Method))
        {
            await ctx.Response.Body.WriteAsync(bytes, ct).ConfigureAwait(false);
        }
    }

    private sealed record Registration(string Name, IVirtualFileSystem Fs, IMountStatsSink Stats, DavCredentials? Credentials);

    private sealed class DavRequest(HttpContext ctx, Registration registration, string path, bool trailingSlash)
    {
        public HttpContext Ctx => ctx;

        public string Collection => registration.Name;

        public IVirtualFileSystem Fs => registration.Fs;

        public string Path => path;

        public bool TrailingSlash => trailingSlash;
    }

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
