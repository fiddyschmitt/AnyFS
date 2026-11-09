using System.Text;
using System.Text.Json;
using AnyFS.Sdk;
using AnyFS.Sources.Common;

namespace AnyFS.Sources.GitLab;

/// <summary>A pipeline: its number, the ref it ran on, how it went.</summary>
internal sealed record GlPipeline(long Id, string Ref, string Sha, string Status, string? Source, DateTimeOffset? Created, DateTimeOffset? Updated, string WebUrl)
{
    public static GlPipeline Parse(JsonElement e) => new(e.Long("id"), e.Str("ref") ?? "", e.Str("sha") ?? "", e.Str("status") ?? "", e.Str("source"),
        e.Date("created_at"), e.Date("updated_at"), e.Str("web_url") ?? "");

    public bool Finished => Status is "success" or "failed" or "canceled" or "skipped" or "manual";
}

/// <summary>
/// A job of a pipeline: its name, stage and status, its artifacts archive where it kept one, and its log's length where
/// GitLab has archived the log (its "trace" artifact, <c>job.log</c>: a finished job's, not a running one's).
/// </summary>
internal sealed record GlJob(long Id, string Name, string Stage, string Status, DateTimeOffset? Started, DateTimeOffset? Finished, double? Duration, long? ArchiveSize, long? LogSize, DateTimeOffset? ArtifactsExpire, string WebUrl)
{
    public static GlJob Parse(JsonElement e)
    {
        long? archive = null;
        long? log = null;
        foreach (var a in e.Arr("artifacts"))
        {
            switch (a.Str("file_type"))
            {
                case "archive":
                    archive = a.Long("size");
                    break;
                case "trace":
                    log = a.Long("size");
                    break;
            }
        }

        double? duration = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : null;
        return new GlJob(e.Long("id"), e.Str("name") ?? "", e.Str("stage") ?? "", e.Str("status") ?? "", e.Date("started_at"), e.Date("finished_at"), duration, archive, log, e.Date("artifacts_expire_at"), e.Str("web_url") ?? "");
    }

    public bool Done => Status is "success" or "failed" or "canceled" or "skipped" or "manual";
}

/// <summary>A project's newest pipelines (as many as the settings keep): a folder each, named by its number and ref.</summary>
internal sealed class PipelinesFolder(GlProject project, string name) : GlFolder(name, project.LastActivity, project.Created)
{
    public override async Task<IReadOnlyList<GlNode>> ListAsync(GitLabSource source, CancellationToken ct)
    {
        var pipelines = await source.PipelinesAsync(project, ct).ConfigureAwait(false);
        var names = GitLabNames.Unique(pipelines.Select(p => $"{p.Id} {p.Ref}").ToList());
        return pipelines.Select((p, i) => (GlNode)new PipelineFolder(project, p, names[i])).ToList();
    }
}

/// <summary>A pipeline: <c>pipeline.md</c> (its ref, commit, status, times and jobs), and a folder per job.</summary>
internal sealed class PipelineFolder(GlProject project, GlPipeline pipeline, string name) : GlFolder(name, pipeline.Updated, pipeline.Created)
{
    public GlPipeline Pipeline { get; } = pipeline;

    public override async Task<IReadOnlyList<GlNode>> ListAsync(GitLabSource source, CancellationToken ct)
    {
        var jobs = await source.JobsAsync(project, Pipeline, ct).ConfigureAwait(false);
        var names = GitLabNames.Unique(jobs.Select(j => j.Name).ToList());
        var children = new List<GlNode> { new TextFile("pipeline.md", Document(Pipeline, jobs), Pipeline.Updated, Pipeline.Created) };
        children.AddRange(jobs.Select((j, i) => (GlNode)new JobFolder(project, j, names[i])));
        return children;
    }

    private static byte[] Document(GlPipeline p, IReadOnlyList<GlJob> jobs)
    {
        var sb = new StringBuilder();
        sb.Append("# Pipeline ").Append(p.Id).Append(" on ").Append(p.Ref).Append("\n\n");
        GitLabDocuments.Line(sb, "Status", p.Status);
        GitLabDocuments.Line(sb, "Commit", p.Sha);
        GitLabDocuments.Line(sb, "Source", p.Source);
        GitLabDocuments.Line(sb, "Created", GitLabDocuments.Date(p.Created));
        GitLabDocuments.Line(sb, "Updated", GitLabDocuments.Date(p.Updated));
        GitLabDocuments.Line(sb, "Web", p.WebUrl);
        sb.Append("\n---\n\n| Job | Stage | Status | Started | Took |\n|---|---|---|---|---|\n");
        foreach (var j in jobs)
        {
            sb.Append("| ").Append(j.Name.Replace("|", "\\|", StringComparison.Ordinal)).Append(" | ").Append(j.Stage).Append(" | ").Append(j.Status)
                .Append(" | ").Append(GitLabDocuments.Date(j.Started) ?? "").Append(" | ").Append(j.Duration is { } d ? $"{d:0} s" : "").Append(" |\n");
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}

/// <summary>A job: its log (<c>job.log</c>, as GitLab keeps it) and, while they are kept, its artifacts as files.</summary>
internal sealed class JobFolder(GlProject project, GlJob job, string name) : GlFolder(name, job.Finished ?? job.Started, job.Started)
{
    public override async Task<IReadOnlyList<GlNode>> ListAsync(GitLabSource source, CancellationToken ct)
    {
        var children = new List<GlNode> { new JobLog(project, job, await source.LogLengthAsync(project, job, ct).ConfigureAwait(false)) };
        if (job.ArchiveSize is > 0 && (job.ArtifactsExpire is null || job.ArtifactsExpire > DateTimeOffset.UtcNow))
        {
            children.Add(new ArtifactsFolder(project, job, "", "artifacts", job.Finished));
        }

        return children;
    }
}

/// <summary>A folder of a job's artifacts archive, from the archive's own list of files.</summary>
internal sealed class ArtifactsFolder(GlProject project, GlJob job, string path, string name, DateTimeOffset? modified) : GlFolder(name, modified, modified)
{
    public override async Task<IReadOnlyList<GlNode>> ListAsync(GitLabSource source, CancellationToken ct)
    {
        var entries = await source.ArtifactsAsync(project, job, ct).ConfigureAwait(false);
        var prefix = path.Length == 0 ? "" : path + "/";
        var level = new SortedDictionary<string, ZipEntry?>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            if (!e.Path.StartsWith(prefix, StringComparison.Ordinal) || e.Path.Length == prefix.Length)
            {
                continue;
            }

            var rest = e.Path[prefix.Length..];
            var slash = rest.IndexOf('/');
            if (slash >= 0)
            {
                level.TryAdd(rest[..slash], null);
            }
            else if (e.IsDirectory)
            {
                level.TryAdd(rest, null);
            }
            else
            {
                level[rest] = e;
            }
        }

        var raw = level.Keys.ToList();
        var names = GitLabNames.Unique(raw);
        return raw.Select((segment, i) => level[segment] is { } file
            ? (GlNode)new ArtifactFile(project, job, file, names[i], Modified)
            : new ArtifactsFolder(project, job, prefix + segment, names[i], Modified)).ToList();
    }
}

/// <summary>
/// A job's log, <c>job.log</c>: listed with its length (<see cref="GitLabSource.LogLengthAsync"/>), its bytes asked of GitLab
/// only when it is read, through the spool as the artifacts' files are.
/// </summary>
internal sealed class JobLog(GlProject project, GlJob job, long length) : GlFile("job.log", length, job.Finished ?? job.Started, job.Started)
{
    public override Task<IVfsFile> OpenAsync(GitLabSource source, CancellationToken ct) => source.OpenLogAsync(project, job, Length, ct);
}

/// <summary>A file of a job's artifacts: read alone (GitLab takes it out of the archive), never the whole archive.</summary>
internal sealed class ArtifactFile(GlProject project, GlJob job, ZipEntry entry, string name, DateTimeOffset? modified) : GlFile(name, entry.Size, entry.Modified ?? modified, modified)
{
    public override Task<IVfsFile> OpenAsync(GitLabSource source, CancellationToken ct) => source.OpenArtifactAsync(project, job, entry, ct);
}
