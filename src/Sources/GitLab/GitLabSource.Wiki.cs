using AnyFS.Sdk;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.GitLab;

public sealed partial class GitLabSource
{
    /// <summary>A wiki's pages with their content (one request), asked again once the refresh interval has passed.</summary>
    internal Task<List<GlWikiPage>> WikiAsync(WikiOwner owner, CancellationToken ct)
        => Cache.GetAsync($"wiki:{owner.Key}", Refresh, async c =>
        {
            using var doc = await Api.GetAsync($"{owner.Api}/wikis?with_content=1", c).ConfigureAwait(false);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array ? doc.RootElement.EnumerateArray().Select(GlWikiPage.Parse).ToList() : [];
        }, ct);

    /// <summary>Whether a project's wiki has pages: a wiki turned off, or kept from this account, has none.</summary>
    internal async Task<bool> HasWikiPagesAsync(WikiOwner owner, CancellationToken ct)
    {
        try
        {
            return (await WikiAsync(owner, ct).ConfigureAwait(false)).Count > 0;
        }
        catch (VfsException ex) when (ex.Error is VfsError.NotFound or VfsError.AccessDenied)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether a group has a wiki with pages to show: group wikis are GitLab Premium's, so a Free server answers 404 or 403,
    /// and that is asked once an hour at most.
    /// </summary>
    internal async Task<bool> GroupHasWikiAsync(GlGroup group, CancellationToken ct)
    {
        var has = await Cache.GetAsync($"groupwiki:{group.Id}", TimeSpan.FromHours(1), async c => new Box<bool>(await HasWikiPagesAsync(WikiOwner.Of(group), c).ConfigureAwait(false)), ct).ConfigureAwait(false);
        return has.Value;
    }

    private Uri WikiUploadUrl(WikiOwner owner, GlWikiUpload upload) => Api.Address.Page($"{owner.Web}/uploads/{upload.Hash}/{upload.FileName}");

    /// <summary>A wiki attachment's size, or null where GitLab will not give it (no browser session, or gone).</summary>
    internal async Task<long?> WikiUploadSizeAsync(WikiOwner owner, GlWikiUpload upload, CancellationToken ct)
    {
        var size = await Cache.GetAsync($"wikiupload:{owner.Key}:{upload.Hash}/{upload.FileName}", null, async c =>
        {
            try
            {
                var headers = await Api.HeadAsync(WikiUploadUrl(owner, upload), "a wiki attachment", c).ConfigureAwait(false);
                return new Box<long?>(headers.TryGetValue("Content-Length", out var length) && long.TryParse(length, out var n) ? n : null);
            }
            catch (VfsException ex) when (ex.Error is not VfsError.Unavailable)
            {
                Logger.LogInformation("gitlab: the wiki attachment {Hash}/{Name} could not be had: {Error}", upload.Hash, upload.Name, ex.Message);
                return new Box<long?>(null);
            }
        }, ct).ConfigureAwait(false);
        return size.Value;
    }

    /// <summary>A wiki attachment's bytes (through the browser session), spooled and kept by its hash.</summary>
    internal Task<IVfsFile> OpenWikiUploadAsync(WikiOwner owner, GlWikiUpload upload, long length, CancellationToken ct)
        => length == 0
            ? Task.FromResult<IVfsFile>(new MemoryFile([]))
            : Spool.OpenAsync($"gitlab:{Api.Address.Server.Host}:{owner.Key}:wiki:{upload.Hash}/{upload.FileName}", length,
                async c => await ResponseStream.OpenAsync(await Api.OpenAsync(WikiUploadUrl(owner, upload), "a wiki attachment", c).ConfigureAwait(false), c).ConfigureAwait(false), ct);
}
