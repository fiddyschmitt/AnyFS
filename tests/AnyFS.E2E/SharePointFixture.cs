using System.Text.Json.Nodes;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;
using AnyFS.Sources.SharePoint;

namespace AnyFS.E2E;

/// <summary>
/// A SharePoint Server farm of the lab as an upstream for the E2E rows (spec 7.4): the fixture tree is put into
/// its test site's Documents library once, under <see cref="TreePrefix"/>, through the SharePoint Server source
/// itself (from this PC, with the tester's account), and the hosts then serve that library with the same account
/// over NTLM by address, as a user outside the domain would. A farm's rows are Inconclusive while it is down.
/// </summary>
public static class SharePointFixture
{
    public const string Library = "Documents";

    public const string TreePrefix = "e2e";

    private const int Parallel = 8;

    /// <summary>The source for the hosts' settings, after making sure the tree is there; null, with the reason, when the farm is not usable.</summary>
    public static SourceSpec? TryPrepare(SharePointLab lab, out string? unavailable)
    {
        var settings = new SharePointSettings { SiteUrl = lab.SiteUrl, Library = Library, Username = lab.User, Password = lab.Password, Connections = 4 };
        try
        {
            SeedAsync(settings).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is SourceFailedException or VfsException or OperationCanceledException or HttpRequestException)
        {
            unavailable = $"{lab.MachineName} ({lab.SiteUrl}) did not take the fixture tree: {ex.Message}. Is the farm up? (vagrant up anyfs-dc-1 {lab.MachineName})";
            return null;
        }

        unavailable = null;
        return new SourceSpec(lab.Collection, "sharepoint", TreePrefix, new JsonObject
        {
            ["siteUrl"] = lab.SiteUrl,
            ["library"] = Library,
            ["username"] = lab.User,
            ["password"] = lab.Password,   // moved into the host's secret store at its first start (docs/settings-format.md)
            ["connections"] = 4,
        });
    }

    /// <summary>Uploads what is missing or of another size, folder by folder, several files at a time; a second run finds everything there.</summary>
    private static async Task SeedAsync(SharePointSettings settings)
    {
        _ = Fixture.Manifest;   // generates the local tree when it is not there yet
        var local = Fixture.LocalDir;
        await using var source = new SharePointPlugin().CreateSource(settings);
        using var context = new TestSourceContext("sharepoint-e2e");
        using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await source.InitialiseAsync(context, startup.Token);

        var ct = CancellationToken.None;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var gate = new SemaphoreSlim(Parallel);
        var uploaded = 0;
        await EnsureDirectoryAsync(source, TreePrefix, ct);
        foreach (var dir in new[] { local }.Concat(Directory.EnumerateDirectories(local, "*", SearchOption.AllDirectories).OrderBy(d => d.Count(c => c is '/' or '\\')).ThenBy(d => d, StringComparer.Ordinal)))
        {
            var rel = Rel(local, dir);
            if (dir != local)
            {
                await EnsureDirectoryAsync(source, rel, ct);
            }

            var present = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            await foreach (var entry in source.ListAsync(rel, ct))
            {
                if (!entry.IsDirectory)
                {
                    present[entry.Name] = entry.Length;
                }
            }

            var tasks = new List<Task>();
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var info = new FileInfo(file);
                if (present.TryGetValue(info.Name, out var length) && length == info.Length)
                {
                    continue;
                }

                await gate.WaitAsync(ct);
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await UploadAsync(source, VfsPath.Join(rel, info.Name), file, ct);
                        Interlocked.Increment(ref uploaded);
                    }
                    finally
                    {
                        gate.Release();
                    }
                }, ct));
            }

            await Task.WhenAll(tasks);
        }

        if (uploaded > 0)
        {
            Console.WriteLine($"sharepoint fixture: uploaded {uploaded} files to {settings.SiteUrl} {Library}/{TreePrefix} ({sw.Elapsed.TotalSeconds:N0} s)");
        }
    }

    private static async Task EnsureDirectoryAsync(ISource source, string rel, CancellationToken ct)
    {
        if (await source.StatAsync(rel, ct) is null)
        {
            await source.CreateDirectoryAsync(rel, ct);
        }
    }

    private static async Task UploadAsync(ISource source, string rel, string file, CancellationToken ct)
    {
        await using var handle = await source.OpenAsync(rel, VfsOpenMode.Create, ct);
        await using var stream = File.OpenRead(file);
        var buffer = new byte[1 << 20];
        long offset = 0;
        int n;
        while ((n = await stream.ReadAsync(buffer, ct)) > 0)
        {
            await handle.WriteAsync(offset, buffer.AsMemory(0, n), ct);
            offset += n;
        }
    }

    private static string Rel(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        return relative == "." ? TreePrefix : TreePrefix + "/" + relative;
    }
}
