using System.Text.Json.Nodes;

namespace AnyFS.E2E;

/// <summary>
/// The lab's GitLab server as an upstream for the E2E rows (spec 7.8, provisioning/gitlab/README.md): its seed committed the
/// fixture tree to <c>anyfs/tools/app</c> under <c>e2e/</c> (<c>large.bin</c> in Git LFS), so the hosts serve the project's
/// <c>files</c> and the rows find the tree at <see cref="TreePrefix"/>. The source is read-only, and git keeps no empty
/// folder (the tree's <c>emptydir</c> is not there). The Linux and macOS hosts sign in with the tester's token by the server's
/// address (only the lab's DNS knows its name); the Windows host, which reaches the domain through its name resolution
/// rule, with the tester's domain account by the server's name, so anyfs takes a Kerberos ticket from the domain
/// controller (<c>ANYFS_E2E_GITLAB_SIGNIN=token</c> makes every host use the token). Its rows are Inconclusive while the
/// server is down.
/// </summary>
public static class GitLabFixture
{
    public const string Collection = "gitlab";

    public const string Project = "anyfs/tools/app";

    public const string TreePrefix = "files/e2e";

    /// <summary>The source for a host's settings, after making sure GitLab answers; null, with the reason, when it does not.</summary>
    public static SourceSpec? TryPrepare(GitLabLab lab, string os, out string? unavailable)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var response = http.GetAsync(lab.UrlByIp + "/api/v4/version").GetAwaiter().GetResult();
            if (response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) != true)
            {
                unavailable = $"{lab.MachineName} ({lab.UrlByIp}) answered {(int)response.StatusCode}, not as GitLab's API: is GitLab still starting?";
                return null;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            unavailable = $"{lab.MachineName} ({lab.UrlByIp}) did not answer: {ex.Message}. Is it up? (vagrant up anyfs-dc-1 {lab.MachineName})";
            return null;
        }

        unavailable = null;
        var token = os != "windows" || Environment.GetEnvironmentVariable("ANYFS_E2E_GITLAB_SIGNIN") == "token";
        var settings = token
            ? new JsonObject { ["address"] = $"{lab.UrlByIp}/{Project}", ["signInMethod"] = "Access token", ["accessToken"] = lab.Token }
            : new JsonObject { ["address"] = $"{lab.UrlByName}/{Project}", ["signInMethod"] = "Windows account", ["username"] = lab.User, ["password"] = lab.Password };
        return new SourceSpec(Collection, "gitlab", TreePrefix, settings);
    }
}
