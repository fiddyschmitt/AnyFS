using AnyFS.Sdk;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.SharePoint;

// Files written here whose upload did not land: kept, shown, read from here and tried again, in this run and the next.
public abstract partial class SharePointSource
{
    // The source each uploads' folder is served by now, by the folder. A source replaced (its settings edited, a Retry)
    // may have a file open still; closed later, the file's upload is the source's that started in its place (N4). The
    // lock is that hand-over's: a start takes up what waits and becomes the folder's in one step, and a file closed is
    // handed to the source serving the folder, or left to the start that comes, never neither, never both.
    private static readonly Dictionary<string, SharePointSource> UploadsServed = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock UploadsHandover = new();

    // woken when a file starts to wait, so the retries' loop counts its time from then
    private readonly SemaphoreSlim _uploadsDue = new(0);
    private Task _uploadRetries = Task.CompletedTask;

    /// <summary>
    /// How long a file whose upload has failed this many times in a row waits for the next try: 10 s, 30 s, a minute, then
    /// every five minutes, as the host tries a source that failed to start. (A test sets it.)
    /// </summary>
    internal Func<int, TimeSpan> UploadRetryAfter { get; set; } = tries => tries switch
    {
        <= 1 => TimeSpan.FromSeconds(10),
        2 => TimeSpan.FromSeconds(30),
        3 => TimeSpan.FromMinutes(1),
        _ => TimeSpan.FromMinutes(5),
    };

    /// <summary>
    /// What this source serves, for the notes of the uploads that wait: one written while the source served another site,
    /// account or library names a place that is not there in this one, and is not this one's to upload.
    /// </summary>
    private string UploadsKey => $"{Target}|{SignedInAs}|{string.Join(",", (GivenSites ?? []).Select(s => s.ToString()))}|{PinnedLibrary}";

    /// <summary>
    /// At the start: what the last run, or the source this one starts in the place of, left in the uploads' folder is
    /// taken up (<see cref="RecoverUploads"/>), and the folder is this one's from now, so that a file the one before had
    /// open is this one's to upload when it is closed.
    /// </summary>
    private void ServeUploads()
    {
        lock (UploadsHandover)
        {
            RecoverUploads();
            UploadsServed[_uploadDir] = this;
        }
    }

    /// <summary>At the stop, first: the folder is no longer this one's to serve.</summary>
    private void StopServingUploads()
    {
        lock (UploadsHandover)
        {
            if (UploadsServed.TryGetValue(_uploadDir, out var serving) && ReferenceEquals(serving, this))
            {
                UploadsServed.Remove(_uploadDir);
            }
        }
    }

    /// <summary>
    /// The source that serves this one's uploads' folder now, started in this one's place, where it serves the same
    /// site(s), account and library: a note written for another is not its to upload (it stays, as at a start). Under
    /// the hand-over's lock.
    /// </summary>
    private SharePointSource? InMyPlace()
        => UploadsServed.TryGetValue(_uploadDir, out var serving) && !ReferenceEquals(serving, this) && serving.UploadsKey == UploadsKey ? serving : null;

    /// <summary>
    /// A file the source before this one had open, closed since: this one's from now, told how it ends and tried again by
    /// this one. Where this one's start took the same bytes up from their note already (the close and the start came
    /// together), that is the file, and is returned. Under the hand-over's lock.
    /// </summary>
    private PendingUploadFile TakeOver(PendingUploadFile file)
    {
        foreach (var held in _pending.Values)
        {
            if (string.Equals(held.TempPath, file.TempPath, StringComparison.OrdinalIgnoreCase))
            {
                if (!ReferenceEquals(held, file))
                {
                    return held;
                }

                break;
            }
        }

        file.Ended = UploadEnded;
        file.Waits = UploadWaits;
        file.NeverLands = UploadNeverLands;

        // a write of the same file opened in this source since stays the one that waits
        _pending.TryAdd(file.Path, file);
        return file;
    }

    /// <summary>
    /// The upload of a file closed after this source stopped: through the source in its place. It was tried through this
    /// one, failed, and waited by its note for AnyFS's next start (the review of 21-Nov-2026, N4). With none in its place
    /// yet (it is starting) the file waits by its note, which that start takes up; with one that serves something else,
    /// it waits as a note written for another does.
    /// </summary>
    private async Task UploadHandedOverAsync(PendingUploadFile file, CancellationToken ct)
    {
        SharePointSource? next;
        PendingUploadFile? held = null;
        lock (UploadsHandover)
        {
            next = InMyPlace();
            if (next is not null)
            {
                held = next.TakeOver(file);
            }
        }

        if (next is null)
        {
            throw VfsException.Unavailable($"'{file.Path}' was closed after its source stopped: it is kept, and uploaded when the source has started again");
        }

        if (ReferenceEquals(held, file))
        {
            await next.UploadAsync(file, ct).ConfigureAwait(false);
            return;
        }

        // the same bytes, taken up by that start from their note: its try is this close's
        if (!await held!.TryAgainAsync(ct).ConfigureAwait(false) && !held.HasLanded)
        {
            throw VfsException.Unavailable($"'{file.Path}' could not be uploaded: {held.LastError ?? "it was not taken"}");
        }
    }

    /// <summary>
    /// At the start: what the last run left in the uploads' folder. A file with its note was closed then and had not
    /// landed: it waits again, and is tried at once. A file with no note was still being written when that run ended,
    /// half a file, and goes; so does a note with no file, and one that does not fit its file.
    /// </summary>
    private void RecoverUploads()
    {
        var waiting = new List<PendingUploadFile>();
        foreach (var path in Directory.EnumerateFiles(_uploadDir).ToList())
        {
            if (path.EndsWith(PendingUploadFile.NoteSuffix, StringComparison.OrdinalIgnoreCase))
            {
                if (!File.Exists(path[..^PendingUploadFile.NoteSuffix.Length]))
                {
                    Remove(path);
                }

                continue;
            }

            if (!File.Exists(path + PendingUploadFile.NoteSuffix))
            {
                // half a file from a run that ended; or one a client still writes through the source this one replaced
                if (!PendingUploadFile.IsOpenHere(path))
                {
                    Remove(path);
                }

                continue;
            }

            switch (PendingUploadFile.Recover(path, UploadsKey, UploadAsync, Logger, out var file, out var target))
            {
                case PendingUploadFile.Recovered.Waiting:
                    file!.Ended = UploadEnded;
                    file.Waits = UploadWaits;
                    file.NeverLands = UploadNeverLands;
                    if (_pending.TryAdd(file.Path, file))
                    {
                        waiting.Add(file);
                    }
                    else
                    {
                        // two notes for one file (a run that ended between a later write's note and the older one's removal): the first found stays
                        Logger.LogWarning("sharepoint: '{Path}' waits to be uploaded twice over; {File} is left where it is", file.Path, path);
                    }

                    break;
                case PendingUploadFile.Recovered.NotOurs:
                    Logger.LogWarning("sharepoint: '{Path}' was written while this source served something else and was not uploaded then: it is not uploaded now, and its bytes are left in {File}", target, path);
                    break;
                default:
                    Logger.LogWarning("sharepoint: {File} is not a whole write (its note does not fit it): removed", path);
                    Remove(path);
                    Remove(path + PendingUploadFile.NoteSuffix);
                    break;
            }
        }

        if (waiting.Count > 0)
        {
            Logger.LogInformation("sharepoint: {Count} written before AnyFS last stopped wait to be uploaded, and are tried now: {Paths}", waiting.Count, string.Join(", ", waiting.Select(f => f.Path).Take(10)));
            SayWaiting();
        }

        static void Remove(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // open in a source this one replaced, whose client has yet to close it
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>The files that wait, each tried again when its time comes, one at a time, until the source stops.</summary>
    private async Task RetryUploadsAsync(CancellationToken stop)
    {
        try
        {
            while (true)
            {
                DateTimeOffset? next = null;
                foreach (var file in _pending.Values.Where(f => f.IsWaiting).OrderBy(f => f.Tried.At).ToList())
                {
                    var (tries, at) = file.Tried;
                    if (tries == 0 || at + UploadRetryAfter(tries) <= DateTimeOffset.UtcNow)
                    {
                        try
                        {
                            if (await file.TryAgainAsync(stop).ConfigureAwait(false))
                            {
                                Logger.LogInformation("sharepoint: '{Path}' ({Length:N0} bytes) is uploaded now", file.Path, file.Length);
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            Logger.LogWarning(ex, "sharepoint: the upload of '{Path}' could not be tried again", file.Path);
                        }

                        stop.ThrowIfCancellationRequested();
                        if (!file.IsWaiting)
                        {
                            continue;
                        }

                        (tries, at) = file.Tried;
                    }

                    var due = at + UploadRetryAfter(Math.Max(1, tries));
                    next = next is null || due < next ? due : next;
                }

                SayWaiting();
                var wait = next is { } then ? then - DateTimeOffset.UtcNow : Timeout.InfiniteTimeSpan;
                await _uploadsDue.WaitAsync(next is null ? wait : wait < TimeSpan.Zero ? TimeSpan.Zero : wait, stop).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Whether an upload's failure is one that waiting cannot mend: SharePoint ran the request and refused it for what it
    /// is. The account may not write there (its own refusal of the account, not a sign-in it did not take, and not a
    /// lock, which both come as AccessDenied too and may pass); the folder, or the library, is not there; the name is one
    /// it does not take; a file is there where a new one was asked for. Everything else may pass and is tried again: no
    /// answer, a throttle, a lock, a sign-in that lapsed, a server's own failure, a full quota.
    /// </summary>
    internal static bool UploadNeverLands(Exception failure) => failure is VfsException refusal && refusal.Error switch
    {
        VfsError.NotFound or VfsError.AlreadyExists or VfsError.InvalidPath or VfsError.NotSupported or VfsError.IsADirectory or VfsError.NotADirectory => true,
        VfsError.AccessDenied => refusal.InnerException is UnauthorizedAccessException,
        _ => false,
    };

    /// <summary>A file open for writing has ended: landed, unchanged, given up, deleted or refused.</summary>
    private void UploadEnded(PendingUploadFile file, UploadEnd how)
    {
        if (how is UploadEnd.Unchanged or UploadEnd.GivenUp && file.Supersedes is { IsWaiting: true } older)
        {
            // nothing of this open is to land: the write that waited before it is the file again, and goes on waiting
            _pending.TryUpdate(file.Path, older, file);
        }
        else
        {
            _pending.TryRemove(new KeyValuePair<string, PendingUploadFile>(file.Path, file));
            if (how == UploadEnd.GivenUp && file.IsNew)
            {
                // the name it had here while it was open, which SharePoint never had
                Listings.Update(file.Path, null, StringComparison.OrdinalIgnoreCase);
            }
            else if (how == UploadEnd.Refused)
            {
                // what the folder holds is SharePoint's to say now: the name this had here, or the file that is there
                // in its place
                _items.TryRemove(file.Path, out _);
                ForgetListing(VfsPath.Parent(file.Path));
            }
        }

        SayWaiting();
    }

    /// <summary>
    /// An upload failed and its file waits: said on the source, and the retries' loop told. With this source stopped, it
    /// is the source's in its place to try again, where there is one yet; else that one's start takes it up by its note.
    /// </summary>
    private void UploadWaits(PendingUploadFile file, Exception error)
    {
        if (_stopping.IsCancellationRequested)
        {
            SharePointSource? next;
            lock (UploadsHandover)
            {
                next = InMyPlace();
                if (next is not null && !ReferenceEquals(next.TakeOver(file), file))
                {
                    next = null;   // its start took the same bytes up from their note: that file is the one tried
                }
            }

            next?.UploadWaits(file, error);
            return;
        }

        SayWaiting();
        _uploadsDue.Release();
    }

    /// <summary>
    /// The source's standing word on what waits: Degraded with the files named for as long as any does, whatever its other
    /// calls do meanwhile, and over when none does.
    /// </summary>
    private void SayWaiting()
    {
        var waiting = _pending.Values.Where(f => f.IsWaiting).OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();
        HoldDegraded("uploads", waiting.Count switch
        {
            0 => null,
            1 => $"{waiting[0].Path} is not uploaded yet ({waiting[0].LastError}): it is kept and tried again",
            _ => $"{waiting.Count} files are not uploaded yet, {waiting[0].Path} among them ({waiting[0].LastError}): they are kept and tried again",
        });
    }
}
