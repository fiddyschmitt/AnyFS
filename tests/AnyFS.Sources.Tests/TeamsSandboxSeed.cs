using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using AnyFS.Sources.Common;
using AnyFS.Sources.Teams;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnyFS.Sources.Tests;

/// <summary>
/// Seeds the Microsoft 365 developer sandbox's Teams with what the Teams source's live tests read (spec 7.9), as the
/// tester and through the web session's own tokens: the team "AnyFS Lab" (a Microsoft 365 group whose SharePoint site is
/// made by GroupSiteManager.CreateGroupEx, then made a team from inside the site, as its "Add Microsoft Teams" button
/// does); in its General channel a thread with replies, reactions, an edited and a deleted reply, an inline image and a
/// shared file, and files in the channel's folder; the same in the tester's first chat. Run once per sandbox, with
/// ANYFS_SEED_TEAMS=1 beside ANYFS_TEST_TEAMS_ACCOUNT and ANYFS_TEAMS_PROFILES; a run that finds its marker writes nothing.
/// Either way it prints what the chat service stores for the seeded messages, which the source's reading follows.
/// </summary>
[TestClass]
public class TeamsSandboxSeed
{
    private const string Marker = "[anyfs-seed v1]";
    private const string FilesMarker = "[anyfs-seed v1 files]";
    private const string RootText = "A thread with replies, reactions and files";
    private const string ChatText = "A chat message with a reaction";
    private const string Team = "AnyFS Lab";
    private const string Alias = "anyfslab";

    // an 8x8 PNG, the inline image
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAgAAAAICAIAAABLbSncAAAAGElEQVR4nGP4z8CAFTGMSgwkCaKzRwYJAPRpP8F3E4WwAAAAAElFTkSuQmCC";

    // Teams' media service refuses a request without a browser's User-Agent ("the platform id could not be created")
    private readonly HttpClient _http = new() { DefaultRequestHeaders = { { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/138.0.0.0 Safari/537.36 Edg/138.0.0.0" } } };

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [Timeout(900_000)]
    public async Task SeedTheSandbox()
    {
        var account = Environment.GetEnvironmentVariable("ANYFS_TEST_TEAMS_ACCOUNT");
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANYFS_SEED_TEAMS")) || string.IsNullOrEmpty(account))
        {
            Assert.Inconclusive("set ANYFS_SEED_TEAMS=1 with ANYFS_TEST_TEAMS_ACCOUNT and ANYFS_TEAMS_PROFILES to seed the sandbox's Teams");
        }

        var ct = CancellationToken.None;
        var browser = BrowserLocator.Find(null, "ANYFS_TEAMS_BROWSER") ?? throw new AssertInconclusiveException("no browser");
        var profile = WebSignInTarget.ProfileFor("teams-browser", account, "ANYFS_TEAMS_PROFILES");
        var session = new TeamsWebSession(new Uri(TeamsCloud.Global.TeamsHost), browser, profile, account, NullLogger.Instance);
        Task<string> Token(TeamsService s) => session.TokenAsync(s, interactive: false, _ => { }, ct);
        await using var rest = new TeamsRest(new Uri(TeamsCloud.Global.ApiHost), (s, _, c) => session.TokenAsync(s, false, _ => { }, c), 4, NullLogger.Instance);
        var services = await rest.ServicesAsync(ct);

        // the tenant's SharePoint hosts, from the sandbox account's onmicrosoft.com name
        var tenant = account.Split('@')[1].Split('.')[0];
        var spRoot = $"https://{tenant}.sharepoint.com";
        var spMy = $"https://{tenant}-my.sharepoint.com";

        var me = await AggregatorAsync(services, await Token(TeamsService.Aggregator));
        var team = Find(me, Team) ?? await MakeTeamAsync(services, browser, profile, spRoot, await Token(TeamsService.SharePoint), await Token(TeamsService.Aggregator), ct);
        var general = team["channels"]!.AsArray().First(c => c!["isGeneral"]?.GetValue<bool>() == true)!["id"]!.GetValue<string>();
        var chat = me["chats"]!.AsArray().FirstOrDefault()?["id"]?.GetValue<string>();
        Log($"team {Team} {team["id"]}, General {general}; chat {chat ?? "(none)"}");

        var chatToken = await Token(TeamsService.ChatService);
        var sharePoint = await Token(TeamsService.SharePoint);
        await SeedChannelAsync(services, chatToken, sharePoint, spRoot, team, general);
        if (chat is not null)
        {
            await SeedChatAsync(services, chatToken, sharePoint, spMy, account, chat);
        }

        // what the chat service stores for them
        await ShowAsync(services, chatToken, general);
        if (chat is not null)
        {
            await ShowAsync(services, chatToken, chat);
        }
    }

    // --- the team ------------------------------------------------------------------------------------------------------

    private async Task<JsonNode> AggregatorAsync(TeamsRest.Services services, string token)
    {
        var (status, body) = await SendAsync(HttpMethod.Get, $"{services.Aggregator}/api/v3/teams/users/me?isPrefetch=false&enableMembershipSummary=true", $"Bearer {token}");
        Assert.AreEqual(200, status, body);
        return JsonNode.Parse(body)!;
    }

    private static JsonNode? Find(JsonNode me, string name) => me["teams"]!.AsArray().FirstOrDefault(t => t!["displayName"]?.GetValue<string>() == name);

    /// <summary>The group and its site (CreateGroupEx), then a team for it from inside the site, waited for in the aggregator.</summary>
    private async Task<JsonNode> MakeTeamAsync(TeamsRest.Services services, string browser, string profile, string spRoot, string sharePoint, string aggregator, CancellationToken ct)
    {
        var created = await SendAsync(HttpMethod.Post, $"{spRoot}/_api/GroupSiteManager/CreateGroupEx", $"Bearer {sharePoint}",
            new JsonObject { ["displayName"] = Team, ["alias"] = Alias, ["isPublic"] = false, ["optionalParams"] = new JsonObject { ["Description"] = "AnyFS test team (seeded)", ["Owners"] = new JsonArray() } });
        Log($"CreateGroupEx: {created.Status} {Trim(created.Body)}");

        // the team, from the site with the browser's SharePoint sign-in: the call runs on in the page, polled
        var launch = await DevToolsBrowser.LaunchAsync(browser, profile, headless: true, "about:blank", [], NullLogger.Instance, "seed", ct);
        Assert.IsNotNull(launch.Browser, launch.Failure);
        await using (launch.Browser)
        {
            var cdp = launch.Browser.Client;
            var targets = await cdp.SendAsync("Target.getTargets", ct: ct);
            var pageId = targets["targetInfos"]!.AsArray().First(t => t!["type"]!.GetValue<string>() == "page")!["targetId"]!.GetValue<string>();
            var sid = (await cdp.SendAsync("Target.attachToTarget", new JsonObject { ["targetId"] = pageId, ["flatten"] = true }, ct: ct))["sessionId"]!.GetValue<string>();
            await cdp.SendAsync("Page.navigate", new JsonObject { ["url"] = $"{spRoot}/sites/{Alias}" }, sid, ct);
            async Task<string?> EvalAsync(string js) => (await cdp.SendAsync("Runtime.evaluate", new JsonObject { ["expression"] = js, ["returnByValue"] = true }, sid, ct))["result"]?["value"]?.ToString();
            for (var i = 0; i < 60 && await EvalAsync("location.href.startsWith('" + spRoot + "/sites/" + Alias + "') && document.readyState === 'complete'") != "True"; i++)
            {
                await Task.Delay(1000, ct);
            }

            await EvalAsync($$"""
                window.__anyfsTeam = null;
                (async () => {
                  const site = '{{spRoot}}/sites/{{Alias}}';
                  const ctx = await fetch(site + '/_api/contextinfo', { method: 'POST', headers: { Accept: 'application/json;odata=nometadata' } });
                  const digest = (await ctx.json()).FormDigestValue;
                  const r = await fetch(site + '/_api/groupsitemanager/EnsureTeamForGroup', { method: 'POST', headers: { Accept: 'application/json;odata=nometadata', 'X-RequestDigest': digest } });
                  window.__anyfsTeam = r.status + ' ' + (await r.text()).slice(0, 300);
                })();
                'started'
                """);
            string? answer = null;
            for (var i = 0; i < 90 && answer is null or "null" or ""; i++)
            {
                await Task.Delay(2000, ct);
                answer = await EvalAsync("String(window.__anyfsTeam)");
            }

            Log($"EnsureTeamForGroup: {answer}");
        }

        for (var i = 0; i < 36; i++)
        {
            if (Find(await AggregatorAsync(services, aggregator), Team) is { } made)
            {
                return made;
            }

            await Task.Delay(5000, ct);
        }

        throw new AssertFailedException($"the team {Team} did not appear");
    }

    // --- the channel ---------------------------------------------------------------------------------------------------

    /// <summary>In two steps, each skipped once its marker is there: the thread with its replies, reactions, edit and deletion; then the files, the image and a second thread.</summary>
    private async Task SeedChannelAsync(TeamsRest.Services services, string chatToken, string sharePoint, string spRoot, JsonNode team, string channel)
    {
        var messages = await MessagesAsync(services, chatToken, channel);
        var root = IdOf(messages, RootText);
        if (root is null)
        {
            Log("channel: seeding the thread");
            root = await PostAsync(services, chatToken, channel, $"<p>{RootText} {Marker}</p>");
            var thread = $"{channel};messageid={root}";
            await PostAsync(services, chatToken, thread, "<p>A reply</p>");
            var edited = await PostAsync(services, chatToken, thread, "<p>A reply that will be edited</p>");
            await EditAsync(services, chatToken, channel, edited, "<p>A reply, edited</p>");
            await ReactAsync(services, chatToken, channel, root, "like");
            await ReactAsync(services, chatToken, channel, root, "heart");
            var deleted = await PostAsync(services, chatToken, thread, "<p>A reply that will be deleted</p>");
            await DeleteAsync(services, chatToken, channel, deleted);
        }

        if (IdOf(messages, FilesMarker) is not null)
        {
            Log("channel: files already seeded");
            return;
        }

        // files in the channel's folder of the team's library (a subfolder too), one shared in a reply, an inline image
        Log("channel: seeding the files");
        var site = await SiteAsync(spRoot, sharePoint, team["teamSiteInformation"]!["groupId"]!.GetValue<string>());
        await RemoveAsync(site.Url, sharePoint, "General/probe.txt");
        var shared = await PutAsync(site.Url, sharePoint, "General/anyfs-seed.txt", Encoding.UTF8.GetBytes("Seeded by AnyFS for the Teams source's tests.\n"));
        await PutAsync(site.Url, sharePoint, "General/Specs/design.md", Encoding.UTF8.GetBytes("# Design\n\nA file in a subfolder of the channel's folder.\n"));
        var reply = $"{channel};messageid={root}";
        if (IdOf(messages, "A shared file") is null)
        {
            await PostAsync(services, chatToken, reply, "<p>A shared file</p>", Files(site.Url, shared));
        }

        if (IdOf(messages, "An inline image") is null)
        {
            await PostAsync(services, chatToken, reply, $"<p>An inline image:</p><p>{await ImageAsync(services, channel)}</p>");
        }

        await PostAsync(services, chatToken, channel, $"<p>A second thread {FilesMarker}</p>");
    }

    // --- the chat ------------------------------------------------------------------------------------------------------

    private async Task SeedChatAsync(TeamsRest.Services services, string chatToken, string sharePoint, string spMy, string account, string chat)
    {
        var messages = await MessagesAsync(services, chatToken, chat);
        if (IdOf(messages, ChatText) is null)
        {
            Log("chat: seeding the messages");
            var first = await PostAsync(services, chatToken, chat, $"<p>{ChatText} {Marker}</p>");
            await ReactAsync(services, chatToken, chat, first, "laugh");
            var edited = await PostAsync(services, chatToken, chat, "<p>A chat message that will be edited</p>");
            await EditAsync(services, chatToken, chat, edited, "<p>A chat message, edited</p>");
            var deleted = await PostAsync(services, chatToken, chat, "<p>A chat message that will be deleted</p>");
            await DeleteAsync(services, chatToken, chat, deleted);
        }

        if (IdOf(messages, FilesMarker) is not null)
        {
            Log("chat: files already seeded");
            return;
        }

        // a file from the tester's OneDrive, as Teams shares one in a chat, and an inline image
        Log("chat: seeding the files");
        var personal = $"{spMy}/personal/{account.Replace('@', '_').Replace('.', '_')}";
        await RemoveAsync(personal, sharePoint, "Microsoft Teams Chat Files/probe.txt");
        var shared = await PutAsync(personal, sharePoint, "Microsoft Teams Chat Files/anyfs-chat-seed.txt", Encoding.UTF8.GetBytes("Shared in a chat by AnyFS's seed.\n"));
        if (IdOf(messages, "A file in a chat") is null)
        {
            await PostAsync(services, chatToken, chat, "<p>A file in a chat</p>", Files(personal, shared));
        }

        if (IdOf(messages, "An image in a chat") is null)
        {
            await PostAsync(services, chatToken, chat, $"<p>An image in a chat:</p><p>{await ImageAsync(services, chat)}</p>");
        }

        await PostAsync(services, chatToken, chat, $"<p>The chat's files are seeded {FilesMarker}</p>");
    }

    // --- the chat service's writes -------------------------------------------------------------------------------------

    private async Task<JsonArray> MessagesAsync(TeamsRest.Services services, string token, string conversation)
    {
        var (status, body) = await SendAsync(HttpMethod.Get, Messages(services, conversation) + "?view=msnp24Equivalent|supportsMessageProperties&pageSize=200&startTime=1", $"Bearer {token}");
        Assert.AreEqual(200, status, body);
        return JsonNode.Parse(body)?["messages"]?.AsArray() ?? [];
    }

    private static string? IdOf(JsonArray messages, string text)
        => messages.FirstOrDefault(m => m?["content"]?.ToString().Contains(text, StringComparison.Ordinal) == true)?["id"]?.ToString();

    private static string Messages(TeamsRest.Services services, string conversation) => $"{services.ChatService}/v1/users/ME/conversations/{Uri.EscapeDataString(conversation)}/messages";

    /// <summary>Posts a message (a reply when the conversation names its thread, <c>;messageid=</c>); its id.</summary>
    private async Task<string> PostAsync(TeamsRest.Services services, string token, string conversation, string html, JsonObject? properties = null)
    {
        var body = new JsonObject
        {
            ["content"] = html,
            ["messagetype"] = "RichText/Html",
            ["contenttype"] = "text",
            ["clientmessageid"] = $"{Random.Shared.NextInt64(1_000_000_000_000_000, 9_000_000_000_000_000)}",
            ["imdisplayname"] = "AnyFS Tester",
        };
        if (properties is not null)
        {
            body["properties"] = properties;
        }

        var (status, answer) = await SendAsync(HttpMethod.Post, Messages(services, conversation), $"Bearer {token}", body);
        Log($"post to {Short(conversation)}: {status} {Trim(answer)}");
        Assert.IsTrue(status is 200 or 201, answer);
        return JsonNode.Parse(answer)?["OriginalArrivalTime"]?.ToString() ?? throw new AssertFailedException($"no message id in {answer}");
    }

    private async Task EditAsync(TeamsRest.Services services, string token, string conversation, string id, string html)
    {
        var (status, answer) = await SendAsync(HttpMethod.Put, $"{Messages(services, conversation)}/{id}", $"Bearer {token}",
            new JsonObject { ["content"] = html, ["messagetype"] = "RichText/Html", ["contenttype"] = "text" });
        Log($"edit {id}: {status} {Trim(answer)}");
    }

    private async Task ReactAsync(TeamsRest.Services services, string token, string conversation, string id, string key)
    {
        var (status, answer) = await SendAsync(HttpMethod.Put, $"{Messages(services, conversation)}/{id}/properties?name=emotions", $"Bearer {token}",
            new JsonObject { ["emotions"] = new JsonObject { ["key"] = key, ["value"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() } });
        Log($"react {key} on {id}: {status} {Trim(answer)}");
    }

    private async Task DeleteAsync(TeamsRest.Services services, string token, string conversation, string id)
    {
        var (status, answer) = await SendAsync(HttpMethod.Delete, $"{Messages(services, conversation)}/{id}?behavior=softDelete", $"Bearer {token}");
        Log($"delete {id}: {status} {Trim(answer)}");
    }

    /// <summary>An image in Teams' media service, readable in the conversation: the <c>img</c> the client puts in a message.</summary>
    private async Task<string> ImageAsync(TeamsRest.Services services, string conversation)
    {
        Assert.IsNotNull(services.SkypeToken, "the authz call gave no skype token");
        var auth = $"skype_token {services.SkypeToken}";

        // the media service takes its "platform id" from the client version (49: Teams for work), as purple-teams sends it
        var (status, answer) = await SendAsync(HttpMethod.Post, $"{services.Ams}/v1/objects/", auth,
            new JsonObject { ["type"] = "pish/image", ["permissions"] = new JsonObject { [conversation] = new JsonArray("read") }, ["filename"] = "anyfs-seed.png", ["sharingMode"] = "Inline" },
            headers: new() { ["x-ms-client-version"] = "49/25113001312" });
        Log($"image object: {status} {Trim(answer)}");
        var id = JsonNode.Parse(answer)!["id"]!.GetValue<string>();
        var (put, putAnswer) = await SendAsync(HttpMethod.Put, $"{services.Ams}/v1/objects/{id}/content/imgpsh", auth, bytes: Convert.FromBase64String(Png));
        Log($"image content: {put} {Trim(putAnswer)}");
        return $"<img itemscope=\"\" itemtype=\"http://schema.skype.com/AMSImage\" src=\"{services.Ams}/v1/objects/{id}/views/imgo\" width=\"8\" height=\"8\" id=\"x_{id}\" alt=\"image\" style=\"vertical-align:bottom\">";
    }

    /// <summary>A message's files property for a SharePoint file, as Teams writes one (a JSON string holding an array).</summary>
    private static JsonObject Files(string siteUrl, DriveFile file)
    {
        var url = Uri.UnescapeDataString(file.WebUrl);
        var type = Path.GetExtension(file.Name).TrimStart('.');
        var entry = new JsonObject
        {
            ["@type"] = "http://schema.skype.com/File",
            ["version"] = 2,
            ["id"] = file.UniqueId,
            ["baseUrl"] = $"{siteUrl.TrimEnd('/')}/",
            ["type"] = type,
            ["title"] = file.Name,
            ["state"] = "active",
            ["objectUrl"] = url,
            ["itemid"] = file.UniqueId,
            ["fileName"] = file.Name,
            ["fileType"] = type,
            ["fileInfo"] = new JsonObject { ["itemId"] = null, ["fileUrl"] = url, ["siteUrl"] = $"{siteUrl.TrimEnd('/')}/", ["serverRelativeUrl"] = new Uri(url).AbsolutePath, ["shareUrl"] = null, ["shareId"] = null },
            ["permissionScope"] = "users",
        };
        return new JsonObject { ["files"] = new JsonArray(entry).ToJsonString() };
    }

    // --- SharePoint (its v2.0 drive API: the Teams client's token may write there, not through the classic REST) --------

    private sealed record Site(string Url, string Documents);

    private sealed record DriveFile(string Name, string WebUrl, string UniqueId);

    private async Task<Site> SiteAsync(string spRoot, string token, string groupId)
    {
        var (status, body) = await SendAsync(HttpMethod.Post, $"{spRoot}/_api/GroupSiteManager/GetSiteStatus", $"Bearer {token}", new JsonObject { ["groupId"] = groupId }, accept: "application/json;odata=nometadata");
        Log($"site of {groupId}: {status} {Trim(body)}");
        var site = JsonNode.Parse(body)!;
        return new Site(site["SiteUrl"]!.GetValue<string>(), site["DocumentsUrl"]!.GetValue<string>());
    }

    /// <summary>Uploads to a path in the site's default library, its folders made as needed; the file's name, address and unique id.</summary>
    private async Task<DriveFile> PutAsync(string siteUrl, string token, string path, byte[] content)
    {
        var (status, body) = await SendAsync(HttpMethod.Put, $"{siteUrl}/_api/v2.0/drive/root:/{EscapePath(path)}:/content", $"Bearer {token}", bytes: content);
        Log($"upload {path}: {status} {Trim(body, 160)}");
        Assert.IsTrue(status is 200 or 201, body);
        var item = JsonNode.Parse(body)!;
        var (_, ids) = await SendAsync(HttpMethod.Get, $"{siteUrl}/_api/v2.0/drive/items/{item["id"]}?$select=sharepointIds", $"Bearer {token}");
        var unique = JsonNode.Parse(ids)?["sharepointIds"]?["listItemUniqueId"]?.GetValue<string>() ?? item["id"]!.GetValue<string>();
        return new DriveFile(item["name"]!.GetValue<string>(), item["webUrl"]!.GetValue<string>(), unique);
    }

    private async Task RemoveAsync(string siteUrl, string token, string path)
    {
        var (status, _) = await SendAsync(HttpMethod.Delete, $"{siteUrl}/_api/v2.0/drive/root:/{EscapePath(path)}", $"Bearer {token}");
        Log($"remove {path}: {status}");
    }

    private static string EscapePath(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    // --- what is stored ------------------------------------------------------------------------------------------------

    private async Task ShowAsync(TeamsRest.Services services, string token, string conversation)
    {
        var (status, body) = await SendAsync(HttpMethod.Get, Messages(services, conversation) + "?view=msnp24Equivalent|supportsMessageProperties&pageSize=40&startTime=1", $"Bearer {token}");
        Log($"== {Short(conversation)}: {status}");
        foreach (var m in JsonNode.Parse(body)?["messages"]?.AsArray() ?? [])
        {
            if (m?["messagetype"]?.GetValue<string>()?.StartsWith("ThreadActivity", StringComparison.Ordinal) == true)
            {
                continue;
            }

            var props = m?["properties"] as JsonObject;
            Log($"{m?["id"]} {m?["messagetype"]} root {m?["rootMessageId"] ?? m?["rootmessageid"]} edited {m?["skypeeditedid"]} content {Trim(m?["content"]?.ToString(), 120)}");
            foreach (var (key, value) in props ?? [])
            {
                if (key is "emotions" or "edittime" or "deletetime" or "files" or "cards" or "mentions" or "annotationsSummary")
                {
                    Log($"    {key}: {Trim(value?.ToJsonString(), 500)}");
                }
            }

            if (props is not null)
            {
                Log($"    property keys: {string.Join(", ", props.Select(p => p.Key))}");
            }

            // an inline image's bytes: with the skype token as a header, and as the cookie purple-teams sends
            var content = m?["content"]?.ToString() ?? "";
            var at = content.IndexOf("AMSImage", StringComparison.Ordinal);
            var src = at < 0 ? null : System.Text.RegularExpressions.Regex.Match(content[at..], "src=\"([^\"]+)\"").Groups[1].Value;
            if (!string.IsNullOrEmpty(src) && services.SkypeToken is { } skype)
            {
                var asHeader = await SendAsync(HttpMethod.Get, src, $"skype_token {skype}", accept: "image/*");
                var asCookie = await SendAsync(HttpMethod.Get, src, null, accept: "image/*", headers: new() { ["Cookie"] = $"skypetoken_asm={skype}" });
                Log($"    image {src}: as a header {asHeader.Status} ({asHeader.Body.Length} chars), as a cookie {asCookie.Status} ({asCookie.Body.Length} chars)");
            }
        }
    }

    // --- plumbing ------------------------------------------------------------------------------------------------------

    private async Task<(int Status, string Body)> SendAsync(HttpMethod method, string url, string? authorization, JsonNode? json = null, byte[]? bytes = null, string accept = "application/json", Dictionary<string, string>? headers = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        request.Headers.Accept.ParseAdd(accept);
        foreach (var (name, value) in headers ?? [])
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        if (json is not null)
        {
            request.Content = new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json");
        }
        else if (bytes is not null)
        {
            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }

        using var response = await _http.SendAsync(request);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private void Log(string line) => TestContext.WriteLine(line);

    private static string Short(string conversation) => conversation.Length <= 40 ? conversation : conversation[..20] + "…" + conversation[^16..];

    private static string Trim(string? s, int max = 300) => s is null ? "(none)" : s.Length <= max ? s : s[..max] + "…";
}
