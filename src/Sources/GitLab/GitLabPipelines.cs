using System.Text;
using System.Text.Json;
using AnyFS.Sdk;

namespace AnyFS.Sources.GitLab;

/// <summary>A pipeline: its number, the ref it ran on, how it went.</summary>
internal sealed record GlPipeline(long Id, string Ref, string Sha, string Status, string? Source, DateTimeOffset? Created, DateTimeOffset? Updated, string WebUrl)
{
    public static GlPipeline Parse(JsonElement e) => new(e.Long("id"), e.Str("ref") ?? "", e.Str("sha") ?? "", e.Str("status") ?? "", e.Str("source"),
        e.Date("created_at"), e.Date("updated_at"), e.Str("web_url") ?? "");

    public bool Finished => Status is "success" or "failed" or "canceled" or "skipped" or "manual";
}

/// <summary>A job of a pipeline: its name, stage and status, and its artifacts archive where it kept one.</summary>
internal sealed record GlJob(long Id, string Name, string Stage, string Status, DateTimeOffset? Started, DateTimeOffset? Finished, double? Duration, long? ArchiveSize, DateTimeOffset? ArtifactsExpire, string WebUrl)
{
    public static GlJob Parse(JsonElement e)
    {
        long? archive = null;
        foreach (var a in e.Arr("artifacts"))
        {
            if (a.Str("file_type") == "archive")
            {
                archive = a.Long("size");
            }
        }

        double? duration = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : null;
        return new GlJob(e.Long("id"), e.Str("name") ?? "", e.Str("stage") ?? "", e.Str("status") ?? "", e.Date("started_at"), e.Date("finished_at"), duration, archive, e.Date("artifacts_expire_at"), e.Str("web_url") ?? "");
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
    public override async Task<IReadOnlyList<GlNode>> ListAsync(GitLabSource source, CancellationToken ct)
    {
        var jobs = await source.JobsAsync(project, pipeline, ct).ConfigureAwait(false);
        var names = GitLabNames.Unique(jobs.Select(j => j.Name).ToList());
        var children = new List<GlNode> { new TextFile("pipeline.md", Document(pipeline, jobs), pipeline.Updated, pipeline.Created) };
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
        var log = await source.TraceAsync(project, job, ct).ConfigureAwait(false);
        var children = new List<GlNode> { new CachedFile("job.log", log.Path, log.Length, job.Finished ?? job.Started, job.Started) };
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

/// <summary>A file the source keeps in its cache folder (a job's log), read from there.</summary>
internal sealed class CachedFile(string name, string path, long length, DateTimeOffset? modified, DateTimeOffset? created) : GlFile(name, length, modified, created)
{
    public override Task<IVfsFile> OpenAsync(GitLabSource source, CancellationToken ct) => Task.FromResult<IVfsFile>(new Local(path, Length));

    private sealed class Local(string path, long length) : IVfsFile
    {
        private readonly Microsoft.Win32.SafeHandles.SafeFileHandle _handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

        public long Length => length;

        public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct)
            => offset >= length ? ValueTask.FromResult(0) : RandomAccess.ReadAsync(_handle, buffer[..(int)Math.Min(buffer.Length, length - offset)], offset, ct);

        public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct) => throw VfsException.NotSupported("writing a job's log");

        public ValueTask SetLengthAsync(long length, CancellationToken ct) => throw VfsException.NotSupported("writing a job's log");

        public ValueTask FlushAsync(CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
        {
            _handle.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>A file of a job's artifacts: read alone (GitLab takes it out of the archive), never the whole archive.</summary>
internal sealed class ArtifactFile(GlProject project, GlJob job, ZipEntry entry, string name, DateTimeOffset? modified) : GlFile(name, entry.Size, entry.Modified ?? modified, modified)
{
    public override Task<IVfsFile> OpenAsync(GitLabSource source, CancellationToken ct) => source.OpenArtifactAsync(project, job, entry, ct);
}
