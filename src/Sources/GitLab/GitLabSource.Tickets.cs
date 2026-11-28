using System.Collections.Concurrent;
using System.Globalization;
using AnyFS.Sdk;
using AnyFS.Sources.Common;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.GitLab;

/// <summary>
/// A project's issues or merge requests as last asked: in the order of their numbers and by number, the newest change, when
/// the whole list was last read, each without its description (<see cref="GlTicket.Whole"/>). Kept among the source's
/// answers, where it says what it weighs (a read of what changed since brings few bytes and holds the whole list) and
/// that the next read builds on it.
/// </summary>
internal sealed record TicketList(IReadOnlyList<GlTicket> Items, IReadOnlyDictionary<long, GlTicket> ByNumber, DateTimeOffset? Newest, DateTimeOffset WholeAt) : IWeighed, IBuiltOn
{
    /// <summary>Each ticket's text, two bytes a character, and what a ticket costs with none.</summary>
    public long Bytes { get; } = Items.Sum(t => 400L + (2L * (t.Title.Length + t.Description.Length + t.WebUrl.Length)));
}

public sealed partial class GitLabSource
{
    private static readonly TimeSpan WholeListEvery = TimeSpan.FromHours(1);

    /// <summary>
    /// A project's issues or merge requests, by number. Read whole once (and every hour, so deleted ones go); between,
    /// asked again once the refresh interval has passed for only those changed since the newest seen (<c>updated_after</c>),
    /// so a project of thousands costs a request a minute while it is open. The list that is read on from is the one the
    /// source's answers hold (<see cref="RequestCache"/>), which may weigh so much and no more: one let go is read whole
    /// when it is next asked for. Every project's list was held beside them in a map of its own for the run, each issue
    /// and merge request with its description, and held again by every folder that had listed them (the review of
    /// 21-Nov-2026, P6). A list now holds each without its description. A list of up to a hundred keeps each of them
    /// whole beside it, by itself, as its one request brought them; a longer one's are read whole as they are come to
    /// (<see cref="WholeTicketAsync"/>).
    /// </summary>
    internal async Task<IReadOnlyList<GlTicket>> TicketsAsync(GlProject project, bool mergeRequests, CancellationToken ct)
        => (await TicketListAsync(project, mergeRequests, ct).ConfigureAwait(false)).Items;

    // issues or merge requests read whole in one request, where a client is going straight through them
    private const int TicketsTogether = 50;

    // a list of no more than this keeps its tickets whole as it is read: it came in one request, and they are few
    private const int WholeWithTheirList = 100;

    private readonly ReadAhead _ticketsAhead = new();

    /// <summary>
    /// An issue or a merge request with its description: as kept, by its number and the time of its last change (so one
    /// changed since is not the one kept); else read with the fifty its number is among, where a client is going straight
    /// through them and asks for them next (<see cref="ReadAhead"/>); else asked for by itself. A long list's were all
    /// kept whole as the list was read, ahead of whoever came to them: text that was let go again, as what was longest
    /// unasked for, just before it was wanted, and then asked for a ticket at a time.
    /// </summary>
    internal async Task<GlTicket> WholeTicketAsync(GlProject project, bool mergeRequest, GlTicket ticket, CancellationToken ct)
    {
        if (ticket.Whole)
        {
            return ticket;
        }

        var key = WholeKey(project, mergeRequest, ticket);
        if (Cache.Peek<GlTicket>(key) is { } kept)
        {
            return kept;
        }

        var place = $"{project.Id}:{(mergeRequest ? "mr" : "issue")}";
        if (_ticketsAhead.Wanted(place))
        {
            var first = ((ticket.Iid - 1) / TicketsTogether * TicketsTogether) + 1;
            await _ticketsAhead.ReadAsync($"{place}:{first}", () => ReadTicketsTogetherAsync(project, mergeRequest, first), ct).ConfigureAwait(false);
            if (Cache.Peek<GlTicket>(key) is { } together)
            {
                return together;
            }
        }

        return await Cache.GetAsync(key, null, async c =>
        {
            using var doc = await Api.GetAsync($"projects/{project.Id}/{(mergeRequest ? "merge_requests" : "issues")}/{ticket.Iid}", c).ConfigureAwait(false);
            return GlTicket.Parse(doc.RootElement);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>The issues or merge requests numbered from <paramref name="first"/>, <see cref="TicketsTogether"/> of them, read whole in one request and kept.</summary>
    private async Task ReadTicketsTogetherAsync(GlProject project, bool mergeRequest, long first)
    {
        try
        {
            var numbers = string.Concat(Enumerable.Range(0, TicketsTogether).Select(n => $"&iids[]={first + n}"));
            await foreach (var e in Api.PagesAsync($"projects/{project.Id}/{(mergeRequest ? "merge_requests" : "issues")}?scope=all&state=all&per_page=100{numbers}", _stopping.Token).ConfigureAwait(false))
            {
                var ticket = GlTicket.Parse(e);
                Cache.Put(WholeKey(project, mergeRequest, ticket), ticket, null, WeightOf(ticket));
            }
        }
        catch (VfsException ex)
        {
            _ticketsAhead.Failed();
            Logger.LogDebug("gitlab: {Project}: {Count} tickets from {First} could not be read together ({Error}); reading them alone for a while", project.FullPath, TicketsTogether, first, ex.Message);
        }
    }

    private static string WholeKey(GlProject project, bool mergeRequest, GlTicket ticket)
        => $"ticket:{project.Id}:{(mergeRequest ? "mr" : "issue")}:{ticket.Iid}@{ticket.Updated:O}";

    /// <summary>What a ticket holds: its text, two bytes a character, and what one costs with none.</summary>
    private static long WeightOf(GlTicket ticket) => 400L + (2L * (ticket.Title.Length + ticket.Description.Length + ticket.WebUrl.Length));

    /// <summary>One of a project's issues or merge requests by its number, from the same list; null where there is none.</summary>
    internal async Task<GlTicket?> TicketAsync(GlProject project, bool mergeRequests, long number, CancellationToken ct)
        => (await TicketListAsync(project, mergeRequests, ct).ConfigureAwait(false)).ByNumber.GetValueOrDefault(number);

    private Task<TicketList> TicketListAsync(GlProject project, bool mergeRequests, CancellationToken ct)
    {
        var key = $"tickets:{project.Id}:{(mergeRequests ? "mr" : "issue")}";
        return Cache.GetAsync<TicketList>(key, Refresh, async (previous, c) =>
        {
            var what = mergeRequests ? "merge_requests" : "issues";
            var state = Settings.OpenOnly ? "opened" : "all";
            var whole = previous is null || previous.Newest is null || DateTimeOffset.UtcNow - previous.WholeAt > WholeListEvery;
            var query = $"projects/{project.Id}/{what}?scope=all&state={state}&per_page=100&order_by=updated_at&sort=asc";
            if (!whole)
            {
                query += "&updated_after=" + Uri.EscapeDataString(previous!.Newest!.Value.AddSeconds(-1).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            }

            var byNumber = whole ? new Dictionary<long, GlTicket>() : new Dictionary<long, GlTicket>(previous!.ByNumber);
            var brought = new List<GlTicket>();
            await foreach (var e in Api.PagesAsync(query, c).ConfigureAwait(false))
            {
                var ticket = GlTicket.Parse(e);
                if (Settings.OpenOnly && ticket.State != "opened")
                {
                    byNumber.Remove(ticket.Iid);
                }
                else
                {
                    if (brought.Count <= WholeWithTheirList)
                    {
                        brought.Add(ticket);
                    }

                    byNumber[ticket.Iid] = ticket with { Description = "", Whole = false };
                }
            }

            // the few a short list brought, or the few changed since, are kept whole as they came: they are asked for next
            if (brought.Count <= WholeWithTheirList)
            {
                foreach (var ticket in brought)
                {
                    Cache.Put(WholeKey(project, mergeRequests, ticket), ticket, null, WeightOf(ticket));
                }
            }

            var items = byNumber.Values.OrderBy(t => t.Iid).ToList();
            var newest = items.Count == 0 ? previous?.Newest : items.Max(t => t.Updated);
            return new TicketList(items, byNumber, newest, whole ? DateTimeOffset.UtcNow : previous!.WholeAt);
        }, ct);
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

                    // the HEAD gave no length: a GET's, else its bytes counted as they come, nothing kept
                    using var response = await Api.OpenAsync(UploadUrl(project, upload, page), "an attachment", c).ConfigureAwait(false);
                    if (ResponseLength.Of(response) is { } given)
                    {
                        return new Box<long?>(given);
                    }

                    await using var body = await response.Content.ReadAsStreamAsync(c).ConfigureAwait(false);
                    return new Box<long?>(await ResponseLength.CountAsync(body, 0, null, c).ConfigureAwait(false));
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
