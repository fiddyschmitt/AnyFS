using System.Collections.Concurrent;
using System.Globalization;
using AnyFS.Sdk;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.GitLab;

/// <summary>A project's issues or merge requests as last asked: by number, the newest change, when the whole list was last read.</summary>
internal sealed record TicketList(IReadOnlyList<GlTicket> Items, DateTimeOffset? Newest, DateTimeOffset WholeAt);

public sealed partial class GitLabSource
{
    private static readonly TimeSpan WholeListEvery = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, TicketList> _tickets = new(StringComparer.Ordinal);

    /// <summary>
    /// A project's issues or merge requests, by number. Read whole once (and every hour, so deleted ones go); between,
    /// asked again once the refresh interval has passed for only those changed since the newest seen (<c>updated_after</c>),
    /// so a project of thousands costs a request a minute while it is open.
    /// </summary>
    internal async Task<IReadOnlyList<GlTicket>> TicketsAsync(GlProject project, bool mergeRequests, CancellationToken ct)
    {
        var key = $"tickets:{project.Id}:{(mergeRequests ? "mr" : "issue")}";
        var list = await Cache.GetAsync(key, Refresh, async c =>
        {
            var what = mergeRequests ? "merge_requests" : "issues";
            var state = Settings.OpenOnly ? "opened" : "all";
            _tickets.TryGetValue(key, out var previous);
            var whole = previous is null || previous.Newest is null || DateTimeOffset.UtcNow - previous.WholeAt > WholeListEvery;
            var query = $"projects/{project.Id}/{what}?scope=all&state={state}&per_page=100&order_by=updated_at&sort=asc";
            if (!whole)
            {
                query += "&updated_after=" + Uri.EscapeDataString(previous!.Newest!.Value.AddSeconds(-1).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            }

            var byNumber = whole ? new Dictionary<long, GlTicket>() : previous!.Items.ToDictionary(t => t.Iid);
            await foreach (var e in Api.PagesAsync(query, c).ConfigureAwait(false))
            {
                var ticket = GlTicket.Parse(e);
                if (Settings.OpenOnly && ticket.State != "opened")
                {
                    byNumber.Remove(ticket.Iid);
                }
                else
                {
                    byNumber[ticket.Iid] = ticket;
                }
            }

            var items = byNumber.Values.OrderBy(t => t.Iid).ToList();
            var newest = items.Count == 0 ? previous?.Newest : items.Max(t => t.Updated);
            var fresh = new TicketList(items, newest, whole ? DateTimeOffset.UtcNow : previous!.WholeAt);
            _tickets[key] = fresh;
            return fresh;
        }, ct).ConfigureAwait(false);
        return list.Items;
    }

    /// <summary>An issue's or merge request's comments, oldest first; asked again when the issue has changed since.</summary>
    internal Task<List<GlNote>> NotesAsync(GlProject project, GlTicket ticket, bool mergeRequest, CancellationToken ct)
        => Cache.GetAsync($"notes:{project.Id}:{(mergeRequest ? "mr" : "issue")}:{ticket.Iid}@{ticket.Updated:O}", null, async c =>
        {
            var what = mergeRequest ? "merge_requests" : "issues";
            var notes = new List<GlNote>();
            await foreach (var e in Api.PagesAsync($"projects/{project.Id}/{what}/{ticket.Iid}/notes?sort=asc&order_by=created_at&per_page=100", c).ConfigureAwait(false))
            {
                notes.Add(GlNote.Parse(e));
            }

            return notes;
        }, ct);

    /// <summary>A merge request's changes, file by file (<c>/diffs</c>, GitLab 15.7 on; <c>/changes</c> before it).</summary>
    internal Task<List<GlDiff>> DiffsAsync(GlProject project, GlTicket ticket, CancellationToken ct)
        => Cache.GetAsync($"diffs:{project.Id}:{ticket.Iid}@{ticket.Updated:O}", null, async c =>
        {
            var diffs = new List<GlDiff>();
            try
            {
                await foreach (var e in Api.PagesAsync($"projects/{project.Id}/merge_requests/{ticket.Iid}/diffs?per_page=100", c).ConfigureAwait(false))
                {
                    diffs.Add(GlDiff.Parse(e));
                }
            }
            catch (VfsException ex) when (ex.Error == VfsError.NotFound && diffs.Count == 0)
            {
                using var changes = await Api.GetAsync($"projects/{project.Id}/merge_requests/{ticket.Iid}/changes", c).ConfigureAwait(false);
                diffs.AddRange(changes.RootElement.Arr("changes").Select(GlDiff.Parse));
            }

            return diffs;
        }, ct);

    /// <summary>
    /// An upload's address: the API's (<c>projects/:id/uploads/:secret/:filename</c>, GitLab 17.4 on), which takes any
    /// sign-in; the project's own page for it on an older GitLab, which takes a browser session.
    /// </summary>
    private Uri UploadUrl(GlProject project, GlUpload upload, bool page)
        => page
            ? Api.Address.Page($"{project.FullPath}/uploads/{upload.Secret}/{upload.FileName}")
            : new Uri(Api.Address.Api, $"projects/{project.Id}/uploads/{upload.Secret}/{upload.FileName}");

    /// <summary>An upload's size (a HEAD request's Content-Length), or null when GitLab no longer has it.</summary>
    internal Task<Box<long?>> UploadSizeAsync(GlProject project, GlUpload upload, CancellationToken ct)
        => Cache.GetAsync($"upload:{project.Id}:{upload.Secret}/{upload.FileName}", null, async c =>
        {
            foreach (var page in new[] { false, true })
            {
                try
                {
                    var headers = await Api.HeadAsync(UploadUrl(project, upload, page), "an attachment", c).ConfigureAwait(false);
                    if (headers.TryGetValue("Content-Length", out var text) && long.TryParse(text, out var length))
                    {
                        return new Box<long?>(length);
                    }

                    // no length given: count the bytes
                    return new Box<long?>((await Api.BytesAsync(UploadUrl(project, upload, page), "an attachment", c).ConfigureAwait(false)).LongLength);
                }
                catch (VfsException ex) when (ex.Error is VfsError.NotFound or VfsError.AccessDenied && !page)
                {
                    // an older GitLab, without the uploads API: its own page for it
                }
                catch (VfsException ex) when (ex.Error is not VfsError.Unavailable)
                {
                    Logger.LogInformation("gitlab: the attachment {Secret}/{Name} could not be had: {Error}", upload.Secret, upload.Name, ex.Message);
                    return new Box<long?>(null);
                }
            }

            return new Box<long?>(null);
        }, ct);

    /// <summary>An upload's bytes, spooled and kept by its secret (an upload never changes).</summary>
    internal Task<IVfsFile> OpenUploadAsync(GlProject project, GlUpload upload, long length, CancellationToken ct)
    {
        if (length == 0)
        {
            return Task.FromResult<IVfsFile>(new MemoryFile([]));
        }

        return Spool.OpenAsync($"gitlab:{Api.Address.Server.Host}:{project.Id}:upload:{upload.Secret}/{upload.FileName}", length, async c =>
        {
            HttpResponseMessage response;
            try
            {
                response = await Api.OpenAsync(UploadUrl(project, upload, page: false), "an attachment", c).ConfigureAwait(false);
            }
            catch (VfsException ex) when (ex.Error is VfsError.NotFound or VfsError.AccessDenied)
            {
                response = await Api.OpenAsync(UploadUrl(project, upload, page: true), "an attachment", c).ConfigureAwait(false);
            }

            return await ResponseStream.OpenAsync(response, c).ConfigureAwait(false);
        }, ct);
    }
}
