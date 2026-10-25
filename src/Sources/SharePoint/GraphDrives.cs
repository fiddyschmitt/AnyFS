using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AnyFS.Sdk;

namespace AnyFS.Sources.SharePoint;

/// <summary>A document library (a Graph drive) of the site.</summary>
internal sealed record DriveInfo(string Id, string Name, long? QuotaTotal, long? QuotaRemaining);

/// <summary>
/// SharePoint's libraries as Microsoft Graph has them, drives and their items, over the shared <see cref="GraphClient"/>:
/// an item as a listing entry, a drive's quota, and a file's content put in one request or through an upload session.
/// </summary>
internal static class GraphDrives
{
    /// <summary>Graph's limit for a one-request upload; bigger files use an upload session.</summary>
    public const long SimpleUploadLimit = 4 * 1024 * 1024;

    /// <summary>An upload session's chunks must be multiples of 320 KiB; 10 MiB is Graph's own suggestion.</summary>
    public const int UploadChunkSize = 32 * 320 * 1024;

    // --- items and drives ----------------------------------------------------------------------------------

    /// <summary>A drive item as a listing entry plus the ids the source needs later.</summary>
    public static (VfsEntry Entry, ItemRef Item) ParseItem(JsonElement item)
    {
        var name = item.GetProperty("name").GetString() ?? "";
        var isFolder = item.TryGetProperty("folder", out _);
        var size = item.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var s) ? s : 0;
        var id = item.GetProperty("id").GetString() ?? "";
        var cTag = item.TryGetProperty("cTag", out var ct) ? ct.GetString() : null;
        var eTag = item.TryGetProperty("eTag", out var et) ? et.GetString() : null;
        var downloadUrl = item.TryGetProperty("@microsoft.graph.downloadUrl", out var du) ? du.GetString() : null;
        DateTimeOffset? created = null, modified = null;
        if (item.TryGetProperty("fileSystemInfo", out var fsi))
        {
            created = ReadTime(fsi, "createdDateTime");
            modified = ReadTime(fsi, "lastModifiedDateTime");
        }

        created ??= ReadTime(item, "createdDateTime");
        modified ??= ReadTime(item, "lastModifiedDateTime");
        var entry = new VfsEntry(name, isFolder, isFolder ? 0 : size, created, modified, VfsAttributes.None, cTag ?? eTag);
        return (entry, new ItemRef(id, cTag, downloadUrl, size, isFolder));
    }

    public static DriveInfo ParseDrive(JsonElement drive)
    {
        long? total = null, remaining = null;
        if (drive.TryGetProperty("quota", out var quota))
        {
            if (quota.TryGetProperty("total", out var t) && t.TryGetInt64(out var tv))
            {
                total = tv;
            }

            if (quota.TryGetProperty("remaining", out var r) && r.TryGetInt64(out var rv))
            {
                remaining = rv;
            }
        }

        return new DriveInfo(drive.GetProperty("id").GetString() ?? "", drive.GetProperty("name").GetString() ?? "", total, remaining);
    }

    private static DateTimeOffset? ReadTime(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && value.TryGetDateTimeOffset(out var time) ? time : null;

    // --- uploads -------------------------------------------------------------------------------------------

    /// <summary>PUT a whole small file (at most <see cref="SimpleUploadLimit"/>) to an item address's <c>/content</c>; the created or updated item comes back.</summary>
    public static async Task<JsonDocument> PutContentAsync(this GraphClient graph, string itemAddress, Func<Stream> openContent, long length, string pathForErrors, CancellationToken ct)
    {
        using var response = await graph.SendAsync(() =>
        {
            var content = new StreamContent(openContent());
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Headers.ContentLength = length;
            return new HttpRequestMessage(HttpMethod.Put, graph.Absolute($"{itemAddress}/content?@microsoft.graph.conflictBehavior=replace")) { Content = content };
        }, HttpCompletionOption.ResponseContentRead, authenticated: true, idempotent: true, ct).ConfigureAwait(false);
        return await graph.ReadJsonAsync(response, pathForErrors, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Uploads a file of any size through an upload session: one request to create it, then
    /// <see cref="UploadChunkSize"/> pieces, each retried on its own. The item comes back with the last piece.
    /// </summary>
    public static async Task<JsonDocument> UploadAsync(this GraphClient graph, string itemAddress, Stream content, long length, string pathForErrors, CancellationToken ct)
    {
        var session = await graph.SendJsonAsync(HttpMethod.Post, $"{itemAddress}/createUploadSession",
            """{"item":{"@microsoft.graph.conflictBehavior":"replace"}}""", pathForErrors, ct).ConfigureAwait(false);
        string uploadUrl;
        using (session)
        {
            uploadUrl = session.RootElement.GetProperty("uploadUrl").GetString() ?? throw new VfsException(VfsError.IoError, $"'{pathForErrors}': Graph returned no upload URL");
        }

        var buffer = new byte[UploadChunkSize];
        long offset = 0;
        try
        {
            while (true)
            {
                var want = (int)Math.Min(buffer.Length, length - offset);
                var read = 0;
                while (read < want)
                {
                    var n = await content.ReadAsync(buffer.AsMemory(read, want - read), ct).ConfigureAwait(false);
                    if (n <= 0)
                    {
                        break;
                    }

                    read += n;
                }

                if (read < want)
                {
                    throw new VfsException(VfsError.IoError, $"'{pathForErrors}': the file shrank while it was being uploaded");
                }

                var from = offset;
                var chunk = read;
                using var response = await graph.SendAsync(() =>
                {
                    var piece = new ByteArrayContent(buffer, 0, chunk);
                    piece.Headers.ContentRange = new ContentRangeHeaderValue(from, from + chunk - 1, length);
                    return new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = piece };
                }, HttpCompletionOption.ResponseContentRead, authenticated: false, idempotent: true, ct).ConfigureAwait(false);

                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw GraphClient.MapError(response.StatusCode, body, pathForErrors, graph.Service);
                }

                offset += chunk;
                if (response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK)
                {
                    return JsonDocument.Parse(body);
                }

                if (offset >= length)
                {
                    throw new VfsException(VfsError.IoError, $"'{pathForErrors}': the upload session ended without the item (HTTP {(int)response.StatusCode})");
                }
            }
        }
        catch (Exception) when (TryCancelSession(graph, uploadUrl))
        {
            throw; // never reached: TryCancelSession returns false
        }
    }

    private static bool TryCancelSession(GraphClient graph, string uploadUrl)
    {
        // best effort, fire and forget: an abandoned session expires by itself after a few days
        _ = graph.SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, uploadUrl), HttpCompletionOption.ResponseContentRead, authenticated: false, idempotent: false, CancellationToken.None)
            .ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully)
                {
                    t.Result.Dispose();
                }
                else
                {
                    _ = t.Exception;
                }
            });
        return false;
    }
}
