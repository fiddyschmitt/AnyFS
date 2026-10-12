using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AnyFS.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace AnyFS.Mounts.Http;

/// <summary>
/// One Kestrel listener shared by every HTTP mount on the same address and port. No routing, no MVC:
/// a single request delegate (trim-safe), with Kestrel's own file result doing ranges, ETags and 304s.
/// </summary>
internal sealed class HttpServer : IAsyncDisposable
{
    private static readonly object SinkKey = new();
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();
    private readonly WebApplication _app;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, Registration> _collections = new(StringComparer.OrdinalIgnoreCase);

    private HttpServer(IPAddress address, int port, ILogger logger)
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

    public static async Task<HttpServer> StartAsync(IPAddress address, int port, ILogger logger, CancellationToken ct)
    {
        var server = new HttpServer(address, port, logger);
        try
        {
            await server._app.StartAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await server._app.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"could not listen on {address}:{port}: {ex.GetBaseException().Message}", ex);
        }

        logger.LogInformation("HTTP server listening on {Address}:{Port}", address, port);
        return server;
    }

    public void Register(string collectionName, IVirtualFileSystem fs, IMountStatsSink stats)
    {
        if (!_collections.TryAdd(collectionName, new Registration(collectionName, fs, stats)))
        {
            throw new InvalidOperationException($"'{collectionName}' is already served on port {Port} by another mount");
        }
    }

    public void Unregister(string collectionName) => _collections.TryRemove(collectionName, out _);

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

        _logger.LogInformation("HTTP server on {Address}:{Port} stopped", Address, Port);
    }

    private async Task HandleAsync(HttpContext ctx)
    {
        var ct = ctx.RequestAborted;
        if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method))
        {
            ctx.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            ctx.Response.Headers.Allow = "GET, HEAD";
            return;
        }

        var path = ctx.Request.Path.Value ?? "/";
        var trimmed = path.TrimStart('/');
        if (trimmed.Length == 0)
        {
            await WriteIndexAsync(ctx, ct).ConfigureAwait(false);
            return;
        }

        var slash = trimmed.IndexOf('/');
        var collectionName = slash < 0 ? trimmed : trimmed[..slash];
        var rest = slash < 0 ? "" : trimmed[(slash + 1)..];
        if (!_collections.TryGetValue(collectionName, out var registration))
        {
            await ErrorAsync(ctx, StatusCodes.Status404NotFound, $"There is no collection named '{collectionName}' here.", ct).ConfigureAwait(false);
            return;
        }

        AttachConnection(ctx, registration);
        if (slash < 0)
        {
            Redirect(ctx, "/" + Uri.EscapeDataString(collectionName) + "/");
            return;
        }

        if (!VfsPath.TryNormalize(rest, out var vfsPath, out var reason))
        {
            await ErrorAsync(ctx, StatusCodes.Status400BadRequest, reason, ct).ConfigureAwait(false);
            return;
        }

        var wantsDirectory = rest.Length == 0 || rest.EndsWith('/');
        try
        {
            var entry = await registration.Fs.StatAsync(vfsPath, ct).ConfigureAwait(false);
            if (entry is null)
            {
                await ErrorAsync(ctx, StatusCodes.Status404NotFound, "Not found.", ct).ConfigureAwait(false);
                return;
            }

            if (entry.IsDirectory)
            {
                if (!wantsDirectory)
                {
                    Redirect(ctx, UrlFor(collectionName, vfsPath, directory: true) + QueryOf(ctx));
                    return;
                }

                await WriteDirectoryAsync(ctx, registration, vfsPath, ct).ConfigureAwait(false);
                return;
            }

            if (wantsDirectory)
            {
                await ErrorAsync(ctx, StatusCodes.Status404NotFound, "Not found.", ct).ConfigureAwait(false);
                return;
            }

            await SendFileAsync(ctx, registration, vfsPath, entry, ct).ConfigureAwait(false);
        }
        catch (VfsException ex) when (!ctx.Response.HasStarted)
        {
            var status = ex.Error switch
            {
                VfsError.NotFound or VfsError.NotADirectory or VfsError.IsADirectory => StatusCodes.Status404NotFound,
                VfsError.AccessDenied => StatusCodes.Status403Forbidden,
                VfsError.InvalidPath => StatusCodes.Status400BadRequest,
                VfsError.Unavailable => StatusCodes.Status503ServiceUnavailable,
                _ => StatusCodes.Status500InternalServerError,
            };
            if (status >= 500)
            {
                _logger.LogWarning("HTTP {Method} {Path}: {Error}", ctx.Request.Method, path, ex.Message);
            }

            await ErrorAsync(ctx, status, ex.Message, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

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

    private async Task SendFileAsync(HttpContext ctx, Registration registration, string vfsPath, VfsEntry entry, CancellationToken ct)
    {
        var file = await registration.Fs.OpenAsync(vfsPath, VfsOpenMode.Read, ct).ConfigureAwait(false);
        var stream = new VfsFileStream(file, ownsFile: true);
        var tag = entry.ETag is { Length: > 0 } ? entry.ETag.Trim('"') : $"{entry.Length:x}-{(entry.Modified?.UtcTicks ?? 0):x}";
        var etag = new EntityTagHeaderValue('"' + tag + '"');
        var contentType = ContentTypes.TryGetContentType(entry.Name, out var known) ? known : "application/octet-stream";
        ctx.Response.Headers.ContentDisposition = "inline; filename*=UTF-8''" + Uri.EscapeDataString(entry.Name);
        var result = Results.File(stream, contentType, fileDownloadName: null, lastModified: entry.Modified, entityTag: etag, enableRangeProcessing: true);
        await result.ExecuteAsync(ctx).ConfigureAwait(false);
    }

    private static async Task WriteDirectoryAsync(HttpContext ctx, Registration registration, string vfsPath, CancellationToken ct)
    {
        var entries = new List<VfsEntry>();
        await foreach (var entry in registration.Fs.ListAsync(vfsPath, ct).ConfigureAwait(false))
        {
            entries.Add(entry);
        }

        var sort = ctx.Request.Query["sort"].ToString().ToLowerInvariant();
        var descending = ctx.Request.Query["dir"].ToString().Equals("desc", StringComparison.OrdinalIgnoreCase);
        IEnumerable<VfsEntry> ordered = sort switch
        {
            "size" => entries.OrderBy(e => e.Length),
            "modified" => entries.OrderBy(e => e.Modified ?? DateTimeOffset.MinValue),
            _ => entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase),
        };
        if (descending)
        {
            ordered = ordered.Reverse();
        }

        var sorted = ordered.OrderByDescending(e => e.IsDirectory).ToList();
        if (ctx.Request.Query["format"].ToString().Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            var items = sorted.Select(e => new ListingItem(e.Name, e.IsDirectory ? "directory" : "file", e.IsDirectory ? null : e.Length, e.Modified, Uri.EscapeDataString(e.Name) + (e.IsDirectory ? "/" : ""))).ToList();
            await WriteJsonAsync(ctx, items, HttpJsonContext.Default.ListListingItem, ct).ConfigureAwait(false);
            return;
        }

        var title = "/" + registration.Name + (vfsPath.Length == 0 ? "/" : "/" + vfsPath + "/");
        var sb = new StringBuilder();
        BeginPage(sb, title);
        sb.Append("<h1>");
        var crumbPath = "";
        sb.Append("<a href=\"/\">anyfs</a> / ");
        sb.Append($"<a href=\"{UrlFor(registration.Name, "", true)}\">{Html(registration.Name)}</a> / ");
        foreach (var segment in VfsPath.Split(vfsPath))
        {
            crumbPath = VfsPath.Join(crumbPath, segment);
            sb.Append($"<a href=\"{UrlFor(registration.Name, crumbPath, true)}\">{Html(segment)}</a> / ");
        }

        sb.Append("</h1>\n<table>\n<thead><tr>");
        sb.Append($"<th><a href=\"{SortLink("name", sort, descending)}\">Name</a></th>");
        sb.Append($"<th class=\"num\"><a href=\"{SortLink("size", sort, descending)}\">Size</a></th>");
        sb.Append($"<th><a href=\"{SortLink("modified", sort, descending)}\">Modified</a></th>");
        sb.Append("</tr></thead>\n<tbody>\n");
        if (vfsPath.Length > 0)
        {
            sb.Append("<tr><td><a href=\"../\">../</a></td><td class=\"num\"></td><td></td></tr>\n");
        }

        foreach (var e in sorted)
        {
            var href = Uri.EscapeDataString(e.Name) + (e.IsDirectory ? "/" : "");
            sb.Append($"<tr><td><a href=\"{href}\">{Html(e.Name)}{(e.IsDirectory ? "/" : "")}</a></td>");
            sb.Append($"<td class=\"num\" title=\"{(e.IsDirectory ? "" : e.Length.ToString())}\">{(e.IsDirectory ? "-" : HumanSize(e.Length))}</td>");
            sb.Append($"<td>{(e.Modified is { } m ? m.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss") + " UTC" : "")}</td></tr>\n");
        }

        sb.Append("</tbody></table>\n");
        sb.Append($"<p class=\"foot\">{sorted.Count(e => e.IsDirectory)} folders, {sorted.Count(e => !e.IsDirectory)} files. <a href=\"?format=json\">JSON</a></p>\n");
        EndPage(sb);
        await WriteHtmlAsync(ctx, sb.ToString(), ct).ConfigureAwait(false);
    }

    private async Task WriteIndexAsync(HttpContext ctx, CancellationToken ct)
    {
        var names = _collections.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        if (ctx.Request.Query["format"].ToString().Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            var items = names.Select(n => new CollectionItem(n, "/" + Uri.EscapeDataString(n) + "/")).ToList();
            await WriteJsonAsync(ctx, items, HttpJsonContext.Default.ListCollectionItem, ct).ConfigureAwait(false);
            return;
        }

        var sb = new StringBuilder();
        BeginPage(sb, "AnyFS");
        sb.Append("<h1>AnyFS</h1>\n<table><thead><tr><th>Collection</th></tr></thead><tbody>\n");
        foreach (var name in names)
        {
            sb.Append($"<tr><td><a href=\"/{Uri.EscapeDataString(name)}/\">{Html(name)}/</a></td></tr>\n");
        }

        sb.Append("</tbody></table>\n");
        EndPage(sb);
        await WriteHtmlAsync(ctx, sb.ToString(), ct).ConfigureAwait(false);
    }

    private static async Task WriteJsonAsync<T>(HttpContext ctx, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, CancellationToken ct)
    {
        ctx.Response.ContentType = "application/json; charset=utf-8";
        if (HttpMethods.IsHead(ctx.Request.Method))
        {
            return;
        }

        await JsonSerializer.SerializeAsync(ctx.Response.Body, value, typeInfo, ct).ConfigureAwait(false);
    }

    private static async Task WriteHtmlAsync(HttpContext ctx, string html, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(html);
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.ContentLength = bytes.Length;
        if (HttpMethods.IsHead(ctx.Request.Method))
        {
            return;
        }

        await ctx.Response.Body.WriteAsync(bytes, ct).ConfigureAwait(false);
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

    private static void Redirect(HttpContext ctx, string location)
    {
        ctx.Response.StatusCode = StatusCodes.Status301MovedPermanently;
        ctx.Response.Headers.Location = location;
    }

    private static string QueryOf(HttpContext ctx) => ctx.Request.QueryString.HasValue ? ctx.Request.QueryString.Value! : "";

    internal static string UrlFor(string collection, string vfsPath, bool directory)
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

    private static string SortLink(string column, string current, bool descending)
        => $"?sort={column}&dir={(current == column && !descending ? "desc" : "asc")}";

    private static string Html(string text) => WebUtility.HtmlEncode(text);

    internal static string HumanSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }

    private static void BeginPage(StringBuilder sb, string title)
    {
        sb.Append("<!DOCTYPE html>\n<html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.Append($"<title>{Html(title)}</title>\n<style>");
        sb.Append(":root{color-scheme:light dark;--fg:#1b1b1b;--bg:#fff;--muted:#666;--line:#e3e3e3;--link:#0b57d0}");
        sb.Append("@media (prefers-color-scheme:dark){:root{--fg:#e6e6e6;--bg:#1e1e1e;--muted:#9a9a9a;--line:#333;--link:#8ab4f8}}");
        sb.Append("body{font:14px/1.5 system-ui,-apple-system,Segoe UI,Roboto,sans-serif;color:var(--fg);background:var(--bg);margin:24px auto;max-width:1000px;padding:0 16px}");
        sb.Append("h1{font-size:18px;font-weight:600;word-break:break-all}a{color:var(--link);text-decoration:none}a:hover{text-decoration:underline}");
        sb.Append("table{border-collapse:collapse;width:100%}th,td{text-align:left;padding:6px 10px;border-bottom:1px solid var(--line);vertical-align:top}");
        sb.Append("th{font-weight:600;color:var(--muted)}td.num,th.num{text-align:right;white-space:nowrap;font-variant-numeric:tabular-nums}td:first-child{word-break:break-all}");
        sb.Append(".foot{color:var(--muted)}</style></head><body>\n");
    }

    private static void EndPage(StringBuilder sb) => sb.Append("</body></html>\n");

    private sealed record Registration(string Name, IVirtualFileSystem Fs, IMountStatsSink Stats);

    /// <summary>Routes Kestrel's own log lines into the mount's logger.</summary>
    private sealed class ForwardingLoggerProvider(ILogger target) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => target;

        public void Dispose()
        {
        }
    }

    /// <summary>No console lifetime: the host owns Ctrl+C handling, not the embedded web host.</summary>
    private sealed class SilentLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

internal sealed record ListingItem(string Name, string Type, long? Size, DateTimeOffset? Modified, string Href);

internal sealed record CollectionItem(string Name, string Href);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<ListingItem>))]
[JsonSerializable(typeof(List<CollectionItem>))]
internal sealed partial class HttpJsonContext : JsonSerializerContext
{
}
