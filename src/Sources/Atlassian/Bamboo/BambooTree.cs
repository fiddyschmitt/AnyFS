using AnyFS.Sdk;
using AnyFS.Sources.Common;

namespace AnyFS.Sources.Atlassian.Bamboo;

/// <summary>
/// One thing in a Bamboo source's tree (spec 7.12): a folder (the server, a project, a plan, its branches, a build, a job,
/// its artifacts) or a file (a document the source makes, a log or an artifact's file Bamboo serves). A path is found from
/// the root a folder at a time; a folder finds a child by its name, a plan folder a build by the number its name starts with.
/// </summary>
internal abstract class BmNode(string name)
{
    public string Name { get; } = name;

    public abstract VfsEntry Entry { get; }
}

internal abstract class BmFolder(string name, DateTimeOffset? modified = null, DateTimeOffset? created = null) : BmNode(name)
{
    public DateTimeOffset? Modified { get; } = modified;

    public DateTimeOffset? Created { get; } = created;

    public override VfsEntry Entry => VfsEntry.Directory(Name, Modified, Created);

    public abstract Task<IReadOnlyList<BmNode>> ListAsync(BambooSource source, CancellationToken ct);

    /// <summary>The child named <paramref name="name"/> (in either case), or null: by default from the listing.</summary>
    public virtual async Task<BmNode?> FindAsync(BambooSource source, string name, CancellationToken ct)
        => (await ListAsync(source, ct).ConfigureAwait(false)).FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

internal abstract class BmFile(string name, long length, DateTimeOffset? modified, DateTimeOffset? created = null) : BmNode(name)
{
    public long Length { get; } = length;

    public override VfsEntry Entry => VfsEntry.File(Name, Length, modified, created ?? modified);

    public abstract Task<IVfsFile> OpenAsync(BambooSource source, CancellationToken ct);
}

/// <summary>A file the source makes itself and holds in memory (a plan or a build as Markdown).</summary>
internal sealed class BmText(string name, byte[] content, DateTimeOffset? modified, DateTimeOffset? created = null) : BmFile(name, content.Length, modified, created)
{
    public override Task<IVfsFile> OpenAsync(BambooSource source, CancellationToken ct) => Task.FromResult<IVfsFile>(new MemoryFile(content, "a file made from Bamboo's data"));
}

/// <summary>A file Bamboo serves at an address (a job's log, an artifact's file), streamed as it is opened.</summary>
internal sealed class BmWebFile(Uri url, long size, string name, DateTimeOffset? time) : BmFile(name, size, time, time)
{
    public Uri Url { get; } = url;

    public override Task<IVfsFile> OpenAsync(BambooSource source, CancellationToken ct) => source.OpenWebAsync(Url, Length, ct);
}

/// <summary>The whole server: a folder per project the account can see, <c>Name (KEY)</c>.</summary>
internal sealed class BambooServerRoot() : BmFolder("")
{
    public override async Task<IReadOnlyList<BmNode>> ListAsync(BambooSource source, CancellationToken ct)
    {
        var projects = await source.ProjectsAsync(ct).ConfigureAwait(false);
        var names = AtlassianNames.Unique(projects.Select(BambooDocuments.ProjectFolderName).ToList());
        return projects.Select((p, i) => (BmNode)new BmProjectFolder(p, names[i])).ToList();
    }
}

/// <summary>A project: a folder per plan, <c>Plan name (PROJ-PLAN)</c>.</summary>
internal sealed class BmProjectFolder(BmProject project, string name) : BmFolder(name)
{
    public BmProject Project { get; } = project;

    public override async Task<IReadOnlyList<BmNode>> ListAsync(BambooSource source, CancellationToken ct)
    {
        var plans = await source.PlansAsync(Project.Key, ct).ConfigureAwait(false);
        var names = AtlassianNames.Unique(plans.Select(BambooDocuments.PlanFolderName).ToList());
        return plans.Select((p, i) => (BmNode)new BmPlanFolder(p, names[i])).ToList();
    }
}

/// <summary>
/// A plan, or a plan branch: <c>plan.md</c>, a folder per build (the newest "Builds kept", <c>#12 Successful</c>), and for
/// a plan with "Plan branches" its <c>branches</c>.
/// </summary>
internal sealed class BmPlanFolder(BmPlan plan, string name) : BmFolder(name)
{
    public BmPlan Plan { get; } = plan;

    public override async Task<IReadOnlyList<BmNode>> ListAsync(BambooSource source, CancellationToken ct)
    {
        var results = await source.ResultsAsync(Plan, ct).ConfigureAwait(false);
        var names = AtlassianNames.Unique(results.Select(BambooDocuments.BuildFolderName).ToList(), [BambooDocuments.PlanFile, BambooDocuments.BranchesFolder]);
        var children = new List<BmNode> { PlanFile(source) };
        children.AddRange(results.Select((r, i) => new BmBuildFolder(Plan, r, names[i])));
        if (HasBranches(source))
        {
            children.Add(new BmBranchesFolder(Plan));
        }

        return children;
    }

    /// <summary>A build by the number its folder's name starts with, read alone: a name a kept listing still gives after the build finished opens it as it is now.</summary>
    public override async Task<BmNode?> FindAsync(BambooSource source, string name, CancellationToken ct)
    {
        if (name.Equals(BambooDocuments.PlanFile, StringComparison.OrdinalIgnoreCase))
        {
            return PlanFile(source);
        }

        if (name.Equals(BambooDocuments.BranchesFolder, StringComparison.OrdinalIgnoreCase))
        {
            return HasBranches(source) ? new BmBranchesFolder(Plan) : null;
        }

        if (AtlassianNames.NumberAfterHash(name) is not { } number)
        {
            return null;
        }

        var result = await source.ResultAsync(Plan, number, ct).ConfigureAwait(false);
        return result is null ? null : new BmBuildFolder(Plan, result, BambooDocuments.BuildFolderName(result));
    }

    private BmText PlanFile(BambooSource source) => new(BambooDocuments.PlanFile, BambooDocuments.Plan(Plan, source.Installation), null);

    private bool HasBranches(BambooSource source) => source.Settings.PlanBranches && !Plan.IsBranch;
}

/// <summary>A plan's branches: a folder per plan branch, <c>feature-x (PROJ-PLAN12)</c>, each a plan of its own.</summary>
internal sealed class BmBranchesFolder(BmPlan plan) : BmFolder(BambooDocuments.BranchesFolder)
{
    public override async Task<IReadOnlyList<BmNode>> ListAsync(BambooSource source, CancellationToken ct)
    {
        var branches = await source.BranchesAsync(plan, ct).ConfigureAwait(false);
        var names = AtlassianNames.Unique(branches.Select(BambooDocuments.PlanFolderName).ToList());
        return branches.Select((b, i) => (BmNode)new BmPlanFolder(b, names[i])).ToList();
    }
}

/// <summary>A build: <c>build.md</c> and a folder per job that ran in it (with "Logs" or "Artifacts").</summary>
internal sealed class BmBuildFolder(BmPlan plan, BmResult result, string name) : BmFolder(name, result.Completed ?? result.Started, result.Started)
{
    public BmResult Result { get; } = result;

    public override async Task<IReadOnlyList<BmNode>> ListAsync(BambooSource source, CancellationToken ct)
    {
        var build = await source.BuildAsync(plan, Result, ct).ConfigureAwait(false);
        var children = new List<BmNode> { new BmText(BambooDocuments.BuildFile, BambooDocuments.Build(plan, build, source.Installation), Modified, Created) };
        if (source.Settings.Logs || source.Settings.Artifacts)
        {
            var names = AtlassianNames.Unique(build.Jobs.Select(j => j.JobName).ToList(), [BambooDocuments.BuildFile]);
            children.AddRange(build.Jobs.Select((j, i) => new BmJobFolder(j, names[i], j.Completed ?? j.Started ?? Modified)));
        }

        return children;
    }
}

/// <summary>A job's result: its log files as Bamboo keeps them (with "Logs") and its <c>artifacts</c> (with "Artifacts", where it kept any).</summary>
internal sealed class BmJobFolder(BmJobResult job, string name, DateTimeOffset? time) : BmFolder(name, time, job.Started ?? time)
{
    public override async Task<IReadOnlyList<BmNode>> ListAsync(BambooSource source, CancellationToken ct)
    {
        var children = new List<BmNode>();
        if (source.Settings.Logs)
        {
            var logs = job.LogFiles.Select(l => Uri.TryCreate(l, UriKind.Absolute, out var url) ? url : new Uri(source.Installation, l)).ToList();
            var sizes = await Task.WhenAll(logs.Select(url => source.SizeAsync(url, ct))).ConfigureAwait(false);
            var names = AtlassianNames.Unique(logs.Select(url => FileName(url, "build.log")).ToList(), [BambooDocuments.ArtifactsFolder]);
            children.AddRange(logs.Select((url, i) => new BmWebFile(url, sizes[i], names[i], Modified)));
        }

        if (source.Settings.Artifacts && job.Artifacts.Count > 0)
        {
            children.Add(new BmArtifactsFolder(job, Modified));
        }

        return children;
    }

    /// <summary>The last segment of an address, as a file name; <paramref name="fallback"/> where it has none.</summary>
    public static string FileName(Uri url, string fallback)
    {
        var last = url.AbsolutePath.TrimEnd('/').Split('/')[^1];
        var name = AtlassianNames.Safe(Uri.UnescapeDataString(last));
        return name.Length == 0 || name == "_" ? fallback : name;
    }
}

/// <summary>A job's artifacts: each a folder of the files it kept, or one file where that is what it is.</summary>
internal sealed class BmArtifactsFolder(BmJobResult job, DateTimeOffset? time) : BmFolder(BambooDocuments.ArtifactsFolder, time, time)
{
    public override async Task<IReadOnlyList<BmNode>> ListAsync(BambooSource source, CancellationToken ct)
    {
        var artifacts = job.Artifacts.Select(a => (Artifact: a, Url: Uri.TryCreate(a.Href, UriKind.Absolute, out var url) ? url : new Uri(source.Installation, a.Href))).ToList();
        var pages = await Task.WhenAll(artifacts.Select(a => source.ArtifactEntriesAsync(a.Url, ct))).ConfigureAwait(false);
        var names = AtlassianNames.Unique(artifacts.Select((a, i) => pages[i] is null ? BmJobFolder.FileName(a.Url, a.Artifact.Name) : a.Artifact.Name).ToList());
        var children = new List<BmNode>(artifacts.Count);
        for (var i = 0; i < artifacts.Count; i++)
        {
            var (artifact, url) = artifacts[i];
            if (pages[i] is null)
            {
                // the address serves a file, not a page of files: the artifact is that one file
                var size = artifact.Size > 0 ? artifact.Size : await source.SizeAsync(url, ct).ConfigureAwait(false);
                children.Add(new BmWebFile(url, size, names[i], Modified));
            }
            else
            {
                children.Add(new BmArtifactDirFolder(url, names[i], Modified));
            }
        }

        return children;
    }
}

/// <summary>A folder of an artifact's files, from the page Bamboo shows for it; its sizes by a HEAD each, kept for the run.</summary>
internal sealed class BmArtifactDirFolder(Uri url, string name, DateTimeOffset? time) : BmFolder(name, time, time)
{
    public override async Task<IReadOnlyList<BmNode>> ListAsync(BambooSource source, CancellationToken ct)
    {
        var entries = await source.ArtifactEntriesAsync(url, ct).ConfigureAwait(false) ?? [];
        var ordered = entries.OrderBy(e => e.IsDirectory ? 0 : 1).ThenBy(e => e.Name, StringComparer.Ordinal).ToList();
        var sizes = await Task.WhenAll(ordered.Select(e => e.IsDirectory ? Task.FromResult(0L) : source.SizeAsync(e.Url, ct))).ConfigureAwait(false);
        var names = AtlassianNames.Unique(ordered.Select(e => e.Name).ToList());
        return ordered.Select((e, i) => e.IsDirectory ? (BmNode)new BmArtifactDirFolder(e.Url, names[i], Modified) : new BmWebFile(e.Url, sizes[i], names[i], Modified)).ToList();
    }
}
