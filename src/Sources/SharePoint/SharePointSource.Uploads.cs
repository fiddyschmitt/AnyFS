using AnyFS.Sdk;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.SharePoint;

// Files written here whose upload did not land: kept, shown, read from here and tried again, in this run and the next.
public abstract partial class SharePointSource
{
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
                Listings.InvalidateParentOf(file.Path);
            }
        }

        SayWaiting();
    }

    /// <summary>An upload failed and its file waits: said on the source, and the retries' loop told.</summary>
    private void UploadWaits(PendingUploadFile file, Exception error)
    {
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
        HoldDegraded(waiting.Count switch
        {
            0 => null,
            1 => $"{waiting[0].Path} is not uploaded yet ({waiting[0].LastError}): it is kept and tried again",
            _ => $"{waiting.Count} files are not uploaded yet, {waiting[0].Path} among them ({waiting[0].LastError}): they are kept and tried again",
        });
    }
}
