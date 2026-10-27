using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace AnyFS.Sources.Tests;

/// <summary>The fake's pipelines, jobs (their logs and artifacts archives) and releases, in spike G's shapes.</summary>
internal sealed partial class FakeGitLab
{
    public sealed record Pipeline(long Id, string Ref, string Status, DateTimeOffset Created, DateTimeOffset Updated);

    /// <summary>A job: its log and its artifacts (path → bytes), zipped as GitLab keeps them.</summary>
    public sealed class Job(long id, string name, string status)
    {
        private byte[]? _archive;

        public long Id { get; } = id;

        public string Name { get; } = name;

        public string Status { get; set; } = status;

        public string Log { get; set; } = "";

        public Dictionary<string, byte[]> Artifacts { get; } = new(StringComparer.Ordinal);

        public DateTimeOffset Started { get; init; } = new(2026, 10, 2, 8, 0, 0, TimeSpan.Zero);

        public byte[] Archive => _archive ??= Zip();

        private byte[] Zip()
        {
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var folder in Artifacts.Keys.SelectMany(Folders).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                {
                    zip.CreateEntry(folder + "/");
                }

                foreach (var (path, bytes) in Artifacts)
                {
                    using var entry = zip.CreateEntry(path, CompressionLevel.Optimal).Open();
                    entry.Write(bytes);
                }
            }

            return buffer.ToArray();
        }

        private static IEnumerable<string> Folders(string path)
        {
            for (var slash = path.IndexOf('/'); slash > 0; slash = path.IndexOf('/', slash + 1))
            {
                yield return path[..slash];
            }
        }
    }

    public sealed record Release(string Tag, string Name, string Description, DateTimeOffset Released, IReadOnlyList<(string Name, string Url)> Links);

    public sealed partial class Project
    {
        public List<Pipeline> PipelineList { get; } = [];

        public Dictionary<long, List<Job>> JobsOf { get; } = [];

        public List<Release> ReleaseList { get; } = [];
    }

    /// <summary>Whether a Range on an artifacts archive is honoured (206); false sends it whole (200).</summary>
    public bool HonoursRanges { get; set; } = true;

    /// <summary>How many requests for an archive came with a Range header.</summary>
    public int RangeRequests { get; private set; }

    private Response? PipelinesAndReleases(Project project, Request r, string[] rest)
    {
        switch (rest)
        {
            case ["pipelines"]:
                return Paged(r, project.PipelineList.OrderByDescending(p => p.Id).Select(p => (JsonNode)new JsonObject
                {
                    ["id"] = p.Id,
                    ["ref"] = p.Ref,
                    ["sha"] = FakeGitLab.Sha1(p.Ref),
                    ["status"] = p.Status,
                    ["source"] = "push",
                    ["created_at"] = p.Created.ToString("O", CultureInfo.InvariantCulture),
                    ["updated_at"] = p.Updated.ToString("O", CultureInfo.InvariantCulture),
                    ["web_url"] = $"{ExternalUrl}/{project.FullPath}/-/pipelines/{p.Id}",
                }));
            case ["pipelines", var id, "jobs"]:
                return project.JobsOf.TryGetValue(long.Parse(id, CultureInfo.InvariantCulture), out var jobs)
                    ? Paged(r, jobs.Select(j => (JsonNode)new JsonObject
                    {
                        ["id"] = j.Id,
                        ["name"] = j.Name,
                        ["stage"] = "test",
                        ["status"] = j.Status,
                        ["started_at"] = j.Started.ToString("O", CultureInfo.InvariantCulture),
                        ["finished_at"] = j.Status is "running" ? null : j.Started.AddSeconds(8).ToString("O", CultureInfo.InvariantCulture),
                        ["duration"] = 8.4,
                        ["artifacts"] = j.Artifacts.Count == 0 ? new JsonArray() : new JsonArray(new JsonObject { ["file_type"] = "archive", ["size"] = j.Archive.Length, ["filename"] = "artifacts.zip" }),
                        ["artifacts_expire_at"] = j.Artifacts.Count == 0 ? null : "2099-01-01T00:00:00Z",
                        ["web_url"] = $"{ExternalUrl}/{project.FullPath}/-/jobs/{j.Id}",
                    }))
                    : Response.NotFound("404 Not found");
            case ["jobs", var id, "trace"]:
                return JobOf(project, id) is { } traced ? new Response(200, Encoding.UTF8.GetBytes(traced.Log), "text/plain") : Response.NotFound("404 Not found");
            case ["jobs", var id, "artifacts"]:
            {
                if (JobOf(project, id) is not { Artifacts.Count: > 0 } job)
                {
                    return Response.NotFound("404 Not found");
                }

                if (r.Headers.TryGetValue("Range", out var range) && range.StartsWith("bytes=", StringComparison.Ordinal))
                {
                    RangeRequests++;
                    if (HonoursRanges)
                    {
                        var parts = range[6..].Split('-');
                        var start = long.Parse(parts[0], CultureInfo.InvariantCulture);
                        var end = Math.Min(long.Parse(parts[1], CultureInfo.InvariantCulture), job.Archive.Length - 1);
                        return new Response(206, job.Archive[(int)start..(int)(end + 1)], "application/octet-stream",
                            new() { ["Content-Range"] = $"bytes {start}-{end}/{job.Archive.Length}" });
                    }
                }

                return new Response(200, job.Archive, "application/octet-stream");
            }

            case ["jobs", var id, "artifacts", .. var path] when path.Length > 0:
                return JobOf(project, id) is { } owner && owner.Artifacts.TryGetValue(string.Join('/', path), out var file)
                    ? new Response(200, file, "text/plain")
                    : Response.NotFound("404 Not found");
            case ["releases"]:
                return Paged(r, project.ReleaseList.OrderByDescending(x => x.Released).Select(x => (JsonNode)new JsonObject
                {
                    ["tag_name"] = x.Tag,
                    ["name"] = x.Name,
                    ["description"] = x.Description,
                    ["created_at"] = x.Released.ToString("O", CultureInfo.InvariantCulture),
                    ["released_at"] = x.Released.ToString("O", CultureInfo.InvariantCulture),
                    ["assets"] = new JsonObject
                    {
                        ["links"] = new JsonArray(x.Links.Select(l => (JsonNode)new JsonObject { ["name"] = l.Name, ["url"] = l.Url, ["direct_asset_url"] = l.Url, ["link_type"] = "other" }).ToArray()),
                    },
                }));
            default:
                return null;
        }
    }

    private static Job? JobOf(Project project, string id) => project.JobsOf.Values.SelectMany(j => j).FirstOrDefault(j => j.Id.ToString(CultureInfo.InvariantCulture) == id);
}
