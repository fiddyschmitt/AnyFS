using AnyFS.Sdk;
using Microsoft.Extensions.Logging;

namespace AnyFS.Sources.GitLab;

/// <summary>A job's log as kept in the cache folder: its file and length.</summary>
internal sealed record TraceFile(string Path, long Length);

public sealed partial class GitLabSource
{
    /// <summary>A project's newest pipelines, as many as the settings keep, asked again once the refresh interval has passed.</summary>
    internal Task<List<GlPipeline>> PipelinesAsync(GlProject project, CancellationToken ct)
        => Cache.GetAsync($"pipelines:{project.Id}", Refresh, async c =>
        {
            var kept = Math.Clamp(Settings.PipelinesKept, 1, 1000);
            var pipelines = new List<GlPipeline>();
            await foreach (var e in Api.PagesAsync($"projects/{project.Id}/pipelines?per_page={Math.Min(kept, 100)}&order_by=id&sort=desc", c).ConfigureAwait(false))
            {
                pipelines.Add(GlPipeline.Parse(e));
                if (pipelines.Count >= kept)
                {
                    break;
                }
            }

            return pipelines;
        }, ct);

    /// <summary>A pipeline's jobs (their latest tries): kept for good once the pipeline has finished, else asked again after the refresh interval.</summary>
    internal Task<List<GlJob>> JobsAsync(GlProject project, GlPipeline pipeline, CancellationToken ct)
        => Cache.GetAsync($"jobs:{project.Id}:{pipeline.Id}@{pipeline.Updated:O}", pipeline.Finished ? null : Refresh, async c =>
        {
            var jobs = new List<GlJob>();
            await foreach (var e in Api.PagesAsync($"projects/{project.Id}/pipelines/{pipeline.Id}/jobs?per_page=100&include_retried=false", c).ConfigureAwait(false))
            {
                jobs.Add(GlJob.Parse(e));
            }

            return jobs.OrderBy(j => j.Id).ToList();
        }, ct);

    /// <summary>
    /// A job's log, downloaded whole into the cache folder (its length is known only once it is: a HEAD says 0): a finished
    /// job's once and kept, a running job's again once the refresh interval has passed.
    /// </summary>
    internal Task<TraceFile> TraceAsync(GlProject project, GlJob job, CancellationToken ct)
        => Cache.GetAsync($"trace:{project.Id}:{job.Id}", job.Done ? null : Refresh, async c =>
        {
            var folder = Directory.CreateDirectory(Path.Combine(Context.CacheDirectory, "logs")).FullName;
            var path = Path.Combine(folder, $"{project.Id}-{job.Id}.log");
            if (job.Done && File.Exists(path))
            {
                return new TraceFile(path, new FileInfo(path).Length);
            }

            var temp = path + ".part";
            using (var response = await Api.OpenAsync($"projects/{project.Id}/jobs/{job.Id}/trace", c).ConfigureAwait(false))
            await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await response.Content.CopyToAsync(file, c).ConfigureAwait(false);
            }

            File.Move(temp, path, overwrite: true);
            return new TraceFile(path, new FileInfo(path).Length);
        }, ct);

    /// <summary>
    /// A job's artifacts, from the archive's central directory read at its end with ranged requests (spike G: GitLab answers
    /// them with 206), never the whole archive; a server that ignores Range sends the whole archive once, and the list is
    /// read from that. Kept for good: a job's artifacts never change.
    /// </summary>
    internal Task<IReadOnlyList<ZipEntry>> ArtifactsAsync(GlProject project, GlJob job, CancellationToken ct)
        => Cache.GetAsync($"artifacts:{project.Id}:{job.Id}", null, async c =>
        {
            byte[]? whole = null;
            var relative = $"projects/{project.Id}/jobs/{job.Id}/artifacts";
            async Task<byte[]> ReadAsync(long offset, int length, CancellationToken token)
            {
                if (whole is null)
                {
                    var (partial, bytes) = await Api.RangeAsync(relative, offset, length, token).ConfigureAwait(false);
                    if (partial)
                    {
                        return bytes;
                    }

                    Logger.LogInformation("gitlab: {Project} job {Job}: the artifacts archive came whole ({Length:N0} bytes), not the range asked for", project.FullPath, job.Id, bytes.Length);
                    whole = bytes;
                }

                return whole.AsSpan((int)offset, (int)Math.Min(length, whole.Length - offset)).ToArray();
            }

            try
            {
                return await ZipDirectory.ReadAsync(job.ArchiveSize ?? 0, ReadAsync, c).ConfigureAwait(false);
            }
            catch (InvalidDataException ex)
            {
                Logger.LogInformation("gitlab: {Project} job {Job}: the artifacts archive could not be listed: {Error}", project.FullPath, job.Id, ex.Message);
                return (IReadOnlyList<ZipEntry>)[];
            }
        }, ct);

    /// <summary>One file of a job's artifacts, which GitLab takes out of the archive; spooled and kept by the job and path.</summary>
    internal Task<IVfsFile> OpenArtifactAsync(GlProject project, GlJob job, ZipEntry entry, CancellationToken ct)
    {
        if (entry.Size == 0)
        {
            return Task.FromResult<IVfsFile>(new MemoryFile([]));
        }

        var path = string.Join('/', entry.Path.Split('/').Select(Uri.EscapeDataString));
        return Spool.OpenAsync($"gitlab:{Api.Address.Server.Host}:{project.Id}:job:{job.Id}:{entry.Path}", entry.Size,
            async c => await ResponseStream.OpenAsync(await Api.OpenAsync($"projects/{project.Id}/jobs/{job.Id}/artifacts/{path}", c).ConfigureAwait(false), c).ConfigureAwait(false), ct);
    }

    /// <summary>A project's releases, newest first, asked again once the refresh interval has passed.</summary>
    internal Task<List<GlRelease>> ReleasesAsync(GlProject project, CancellationToken ct)
        => Cache.GetAsync($"releases:{project.Id}", Refresh, async c =>
        {
            var releases = new List<GlRelease>();
            await foreach (var e in Api.PagesAsync($"projects/{project.Id}/releases?per_page=100", c).ConfigureAwait(false))
            {
                releases.Add(GlRelease.Parse(e));
            }

            return releases;
        }, ct);

    /// <summary>Whether a project has pipelines or releases to show (a project without CI has none; most have no releases).</summary>
    internal async Task<bool> HasAnyAsync(Func<Task<int>> count)
    {
        try
        {
            return await count().ConfigureAwait(false) > 0;
        }
        catch (VfsException ex) when (ex.Error is VfsError.NotFound or VfsError.AccessDenied)
        {
            return false;
        }
    }
}
