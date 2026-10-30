using System.Net;
using System.Text;
using AnyFS.Sdk;
using AnyFS.Sources.Teams;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnyFS.Sources.Tests;

/// <summary>TeamsRest's send path against a fake service (spec 7.9): a refused token is handed back and the call tried once more with a fresh one.</summary>
[TestClass]
public class TeamsRestTests
{
    private const string Authz = """{"regionGtms":{"chatServiceAggregator":"https://agg.test","chatService":"https://chat.test","middleTier":"https://mt.test"}}""";
    private const string Teams = """{"teams":[{"id":"19:team","displayName":"Lab","channels":[{"id":"19:general","displayName":"General","isGeneral":true}]}],"chats":[]}""";

    private sealed class Service(Func<HttpRequestMessage, HttpStatusCode> aggregator) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, body) = request.RequestUri!.AbsolutePath switch
            {
                "/api/authsvc/v1.0/authz" => (HttpStatusCode.OK, Authz),
                "/api/v3/teams/users/me" => aggregator(request) is var code && code == HttpStatusCode.OK ? (code, Teams) : (code, """{"errorCode":"Denied"}"""),
                _ => (HttpStatusCode.NotFound, "{}"),
            };
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    [TestMethod]
    public async Task ARefusedTokenIsHandedBackAndTheCallTriedOnceWithAFreshOne()
    {
        var refusals = new List<(TeamsService Service, string Token)>();
        var service = new Service(r => r.Headers.Authorization?.Parameter == "aggregator-2" ? HttpStatusCode.OK : HttpStatusCode.Unauthorized);
        await using var rest = new TeamsRest(new Uri("https://teams.test"), (s, refused, _) =>
        {
            if (refused is not null)
            {
                refusals.Add((s, refused));
            }

            return Task.FromResult(s == TeamsService.Aggregator ? (refused is null ? "aggregator-1" : "aggregator-2") : "middle-tier");
        }, concurrency: 2, NullLogger.Instance, service);

        var structure = await rest.StructureAsync(CancellationToken.None);

        Assert.AreEqual("Lab", structure.Teams.Single().DisplayName);
        CollectionAssert.AreEqual(new[] { (TeamsService.Aggregator, "aggregator-1") }, refusals);
    }

    [TestMethod]
    public async Task AMessagesReactionsEditAndDeletionAreRead()
    {
        // the shapes the seeded sandbox stores (emotions an array; edittime, deletetime in ms), and emotions as a string
        const string Page = """
            {"messages":[
              {"id":"3","messagetype":"RichText/Html","content":"","from":"https://x/v1/users/ME/contacts/8:orgid:jane","composetime":"2026-10-29T10:03:00.000Z","properties":{"deletetime":"1793273722345"}},
              {"id":"2","messagetype":"RichText/Html","content":"<p>edited</p>","from":"https://x/v1/users/ME/contacts/8:orgid:john","composetime":"2026-10-29T10:02:00.000Z","properties":{"edittime":1793273720290,"emotions":"[{\"key\":\"laugh\",\"users\":[{\"mri\":\"8:orgid:jane\",\"time\":1}]}]"}},
              {"id":"1","messagetype":"RichText/Html","content":"<p>root</p>","from":"https://x/v1/users/ME/contacts/8:orgid:jane","composetime":"2026-10-29T10:01:00.000Z","properties":{"emotions":[{"key":"like","users":[{"mri":"8:orgid:john","time":1,"value":"1"},{"mri":"8:orgid:ann","time":2,"value":"2"}]},{"key":"heart","users":[]}]}}
            ],"_metadata":{}}
            """;
        var service = new PageService(Page);
        await using var rest = new TeamsRest(new Uri("https://teams.test"), (_, _, _) => Task.FromResult("token"), concurrency: 2, NullLogger.Instance, service);

        var page = await rest.MessagesAsync("19:chat", 50, CancellationToken.None);
        var byId = page.Messages.ToDictionary(m => m.Id);

        Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(1793273722345), byId["3"].Deleted);
        Assert.IsNull(byId["3"].Html);
        Assert.AreEqual(DateTimeOffset.FromUnixTimeMilliseconds(1793273720290), byId["2"].Edited);
        Assert.AreEqual("laugh", byId["2"].Reactions.Single().Key, "emotions given as a JSON string");
        var like = byId["1"].Reactions.Single();
        Assert.AreEqual("like", like.Key, "a reaction nobody holds any more (heart, no users) is left out");
        CollectionAssert.AreEqual(new[] { "8:orgid:john", "8:orgid:ann" }, like.Mris.ToArray());
        Assert.IsNull(byId["1"].Edited);
    }

    private sealed class PageService(string page) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.RequestUri!.AbsolutePath == "/api/authsvc/v1.0/authz" ? Authz : page;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private const string Search = """
        {"PrimaryQueryResult":{"RelevantResults":{"TotalRows":2,"Table":{"Rows":[
          {"Cells":[{"Key":"Title","Value":"Mark 8 Project Team"},{"Key":"Path","Value":"https://t.sharepoint.com/sites/Mark8ProjectTeam"},{"Key":"GroupId","Value":"g-mark8"},{"Key":"Description","Value":"The Mark 8"},{"Key":"Created","Value":"2026-07-28T17:19:47.0000000Z"}]},
          {"Cells":[{"Key":"Title","Value":"AnyFS Lab"},{"Key":"Path","Value":"https://t.sharepoint.com/sites/anyfslab"},{"Key":"GroupId","Value":"g-lab"}]}
        ]}}}}
        """;

    /// <summary>A tenant's SharePoint search, SharePoint's directory, and Microsoft Graph's groups (two pages, the second's next link off Graph).</summary>
    private sealed class Directory : HttpMessageHandler
    {
        public List<Uri> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            lock (Asked)
            {
                Asked.Add(uri);
            }

            var path = Uri.UnescapeDataString(uri.AbsolutePath);
            var body = path switch
            {
                "/api/authsvc/v1.0/authz" => Authz,
                "/_api/search/query" => Search,
                "/_api/SP.Directory.DirectorySession/Group('g-mark8')" => """{"isPublic":true,"mail":"Mark8ProjectTeam@t.com","alias":"Mark8ProjectTeam","description":"Welcome to the Mark 8"}""",
                "/_api/SP.Directory.DirectorySession/Group('g-lab')" => """{"isPublic":false,"mail":"anyfslab@t.com","alias":"anyfslab"}""",
                "/v1.0/groups" when uri.Query.Contains("skiptoken", StringComparison.Ordinal) => """
                    {"value":[
                      {"id":"g-sales","displayName":"Sales staff","groupTypes":["DynamicMembership"],"mailEnabled":false,"securityEnabled":true,"membershipRule":"(user.department -eq \"Sales\")","onPremisesSyncEnabled":true},
                      {"id":"g-news","displayName":"All news","groupTypes":[],"mailEnabled":true,"securityEnabled":false,"mail":"news@t.com"},
                      {"id":"g-mesg","displayName":"Mail security","groupTypes":[],"mailEnabled":true,"securityEnabled":true}
                    ],"@odata.nextLink":"https://evil.example/v1.0/groups?$skiptoken=3"}
                    """,
                "/v1.0/groups" => """
                    {"value":[
                      {"id":"g-lab","displayName":"AnyFS Lab","groupTypes":["Unified"],"mailEnabled":true,"securityEnabled":false,"visibility":"Private","resourceProvisioningOptions":["Team"],"mail":"anyfslab@t.com","mailNickname":"anyfslab","createdDateTime":"2026-10-29T01:05:00Z","expirationDateTime":"2027-10-29T01:05:00Z"},
                      {"id":"g-secret","displayName":"Secret project","groupTypes":["Unified"],"mailEnabled":true,"securityEnabled":false,"visibility":"HiddenMembership","resourceProvisioningOptions":[]}
                    ],"@odata.nextLink":"https://graph.microsoft.us/v1.0/groups?$select=id&$skiptoken=2"}
                    """,
                "/v1.0/me/memberOf/microsoft.graph.group" => """
                    {"value":[
                      {"id":"g-lab","displayName":"AnyFS Lab","groupTypes":["Unified"],"mailEnabled":true,"securityEnabled":false,"visibility":"Private","resourceProvisioningOptions":["Team"]},
                      {"id":"g-sales","displayName":"Sales staff","groupTypes":["DynamicMembership"],"mailEnabled":false,"securityEnabled":true}
                    ]}
                    """,
                "/v1.0/me/ownedObjects/microsoft.graph.group" => """{"value":[{"id":"g-lab"}]}""",
                _ => null,
            };
            return Task.FromResult(new HttpResponseMessage(body is null ? HttpStatusCode.NotFound : HttpStatusCode.OK) { Content = new StringContent(body ?? "{}", Encoding.UTF8, "application/json") });
        }
    }

    private static string GraphToken() => $"{B64("{\"alg\":\"none\"}")}.{B64("{\"aud\":\"https://graph.microsoft.us\",\"exp\":9999999999}")}.";

    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static readonly TeamInfo[] Joined =
    [
        new("19:lab", "AnyFS Lab", "g-lab", []) { Owner = true, AccessType = 1, Members = 4 },
        new("19:msft", "MSFT", "g-msft", []) { AccessType = 3, Members = 21 },
    ];

    [TestMethod]
    public async Task WithAGraphTokenEveryGroupOfTheOrganisationIsListed()
    {
        var directory = new Directory();
        await using var rest = new TeamsRest(new Uri("https://teams.test"), (_, _, _) => Task.FromResult("token"), concurrency: 2, NullLogger.Instance, directory,
            sharePointHost: () => "t-my.sharepoint.com", inHand: s => s == TeamsService.Graph ? GraphToken() : "token");
        Assert.IsTrue(rest.GraphReady && rest.SharePointReady);

        var groups = (await rest.AllGroupsAsync(Joined, await rest.GroupSitesAsync(CancellationToken.None), CancellationToken.None))!;

        Assert.AreEqual(GroupsScope.Organisation, groups.Scope);
        Assert.AreEqual("Microsoft Graph", groups.From);
        var byId = groups.Groups.ToDictionary(g => g.Id);
        CollectionAssert.AreEquivalent(new[] { "g-lab", "g-secret", "g-sales", "g-news", "g-mesg" }, byId.Keys.ToArray(), "both pages; not the next link off Graph");
        Assert.IsFalse(directory.Asked.Any(u => u.Host == "evil.example"), "a next link off Graph's hosts is never followed (the token would go with it)");
        Assert.IsTrue(directory.Asked.Where(u => u.AbsolutePath.StartsWith("/v1.0/", StringComparison.Ordinal)).All(u => u.Host == "graph.microsoft.us"), "the token's own Graph");

        Assert.AreEqual(("Microsoft 365 group", "Private", (bool?)true), (byId["g-lab"].Kind, byId["g-lab"].Visibility, byId["g-lab"].Team));
        Assert.AreEqual("https://t.sharepoint.com/sites/anyfslab", byId["g-lab"].SiteUrl, "its site, from SharePoint search");
        Assert.AreEqual(4, byId["g-lab"].Members, "the aggregator's count for a team the account is in");
        Assert.IsNotNull(byId["g-lab"].Expires);
        Assert.AreEqual(("Hidden membership", (bool?)false), (byId["g-secret"].Visibility, byId["g-secret"].Team));
        Assert.AreEqual(("Security group", true, "(user.department -eq \"Sales\")", (bool?)true), (byId["g-sales"].Kind, byId["g-sales"].Dynamic, byId["g-sales"].MembershipRule, byId["g-sales"].OnPremisesSynced));
        Assert.AreEqual(("Distribution list", (string?)null, (bool?)null), (byId["g-news"].Kind, byId["g-news"].Visibility, byId["g-news"].Team));
        Assert.AreEqual("Mail-enabled security group", byId["g-mesg"].Kind);
        CollectionAssert.AreEquivalent(new[] { "g-lab", "g-sales" }, groups.Member.ToArray());
        CollectionAssert.AreEquivalent(new[] { "g-lab" }, groups.Owner.ToArray());

        // the account's own groups, of every kind, without listing every group
        var asked = directory.Asked.Count;
        var mine = await rest.MyGroupsAsync(Joined, CancellationToken.None);
        Assert.AreEqual(GroupsScope.Yours, mine.Scope);
        CollectionAssert.AreEquivalent(new[] { "g-lab", "g-sales" }, mine.Groups.Select(g => g.Id).ToArray());
        Assert.AreEqual(("Security group", true), (mine.Groups.Single(g => g.Id == "g-sales").Kind, mine.Member.Contains("g-sales")));
        CollectionAssert.AreEquivalent(new[] { "g-lab" }, mine.Owner.ToArray());
        Assert.IsFalse(directory.Asked.Skip(asked).Any(u => u.AbsolutePath == "/v1.0/groups"), "not the organisation's every group");
    }

    [TestMethod]
    public async Task WithoutAGraphTokenTheGroupsSharePointShowsAreListed()
    {
        var directory = new Directory();
        await using var rest = new TeamsRest(new Uri("https://teams.test"), (_, _, _) => Task.FromResult("token"), concurrency: 2, NullLogger.Instance, directory,
            sharePointHost: () => "t-my.sharepoint.com", inHand: s => s == TeamsService.SharePoint ? "token" : null);
        Assert.IsFalse(rest.GraphReady);

        var groups = (await rest.AllGroupsAsync(Joined, await rest.GroupSitesAsync(CancellationToken.None), CancellationToken.None))!;

        Assert.AreEqual(GroupsScope.Visible, groups.Scope);
        Assert.AreEqual("SharePoint", groups.From);
        var byId = groups.Groups.ToDictionary(g => g.Id);
        Assert.AreEqual(("Public", (bool?)null, "Mark8ProjectTeam@t.com"), (byId["g-mark8"].Visibility, byId["g-mark8"].Team, byId["g-mark8"].Mail), "SharePoint's directory: public; whether it has a team, unknown");
        Assert.AreEqual(("Private", (bool?)true, 4), (byId["g-lab"].Visibility, byId["g-lab"].Team, byId["g-lab"].Members));
        Assert.AreEqual(("Org-wide", (bool?)true, 21), (byId["g-msft"].Visibility, byId["g-msft"].Team, byId["g-msft"].Members), "a team with no site, from the aggregator");
        CollectionAssert.AreEquivalent(new[] { "g-lab", "g-msft" }, groups.Member.ToArray());
        CollectionAssert.AreEquivalent(new[] { "g-lab" }, groups.Owner.ToArray());
        Assert.IsFalse(directory.Asked.Any(u => u.AbsolutePath.StartsWith("/v1.0/", StringComparison.Ordinal)), "no Graph without its token");
    }

    [TestMethod]
    public async Task WithNeitherTokenTheTeamsOwnGroupsAreListedAndNothingIsAsked()
    {
        var directory = new Directory();
        await using var rest = new TeamsRest(new Uri("https://teams.test"), (_, _, _) => Task.FromResult("token"), concurrency: 2, NullLogger.Instance, directory,
            sharePointHost: () => null, inHand: _ => null);
        Assert.IsFalse(rest.SharePointReady || rest.GraphReady);

        Assert.IsNull(await rest.AllGroupsAsync(Joined, sites: null, CancellationToken.None), "no list of every group to give");
        var groups = await rest.MyGroupsAsync(Joined, CancellationToken.None);

        Assert.AreEqual(GroupsScope.Teams, groups.Scope);
        Assert.AreEqual(("Private", (bool?)true, 4), (groups.Groups.Single(g => g.Id == "g-lab").Visibility, groups.Groups.Single(g => g.Id == "g-lab").Team, groups.Groups.Single(g => g.Id == "g-lab").Members));
        Assert.AreEqual("Org-wide", groups.Groups.Single(g => g.Id == "g-msft").Visibility);
        CollectionAssert.AreEquivalent(new[] { "g-lab" }, groups.Owner.ToArray());
        Assert.AreEqual(0, directory.Asked.Count, "no SharePoint and no Graph: nothing to ask");
    }

    /// <summary>
    /// Microsoft Graph's file side, shaped as the sandbox answered it (30-Oct-2026): a channel's filesFolder (and one a team
    /// with no site has not), a drive by its site's path and by its group, a folder's children over two pages (a third page's
    /// link off Graph), an item by path, the shares API, the organisation's Microsoft 365 groups with the account's
    /// memberships (refused, 403, without Group.Read.All), and Microsoft Search's group sites, whose second page still says
    /// there are more (as it is reported to on large sets).
    /// </summary>
    private sealed class GraphFiles : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Uri, string? Bearer, string? Body)> Asked { get; } = [];

        /// <summary>Whether Graph lists the groups (a token with Group.Read.All); else it refuses them, 403.</summary>
        public bool ListsGroups { get; init; } = true;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Asked)
            {
                Asked.Add((request.Method, uri, request.Headers.Authorization?.Parameter, body));
            }

            var path = Uri.UnescapeDataString(uri.AbsolutePath);
            if (!ListsGroups && (path == "/v1.0/groups" || path.StartsWith("/v1.0/me/memberOf", StringComparison.Ordinal)))
            {
                return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("""{"error":{"code":"Authorization_RequestDenied","message":"Insufficient privileges to complete the operation."}}""", Encoding.UTF8, "application/json") };
            }

            var json = path switch
            {
                "/v1.0/me/memberOf/microsoft.graph.group" => """{"value":[{"id":"g-lab"},{"id":"g-private-in"}]}""",
                "/v1.0/groups" when Uri.UnescapeDataString(uri.Query).Contains("groupTypes/any(c:c eq 'Unified')", StringComparison.Ordinal) => """
                    {"value":[
                      {"id":"g-lab","displayName":"AnyFS Lab","visibility":"Private","createdDateTime":"2026-10-29T01:05:00Z"},
                      {"id":"g-mark8","displayName":"Mark 8 Project Team","visibility":"Public","description":"The Mark 8"},
                      {"id":"g-private-in","displayName":"Board","visibility":"Private"},
                      {"id":"g-private-out","displayName":"Secret project","visibility":"Private"},
                      {"id":"g-hidden-out","displayName":"Hidden","visibility":"HiddenMembership"}
                    ]}
                    """,
                "/v1.0/teams/g-lab/channels/19:general@thread.tacv2/filesFolder" => """{"name":"General","webUrl":"https://t.sharepoint.com/sites/lab/Shared%20Documents/General","parentReference":{"driveId":"b!lab"}}""",
                "/v1.0/drives/b!lab/root:/General:/children" when !uri.Query.Contains("skiptoken", StringComparison.Ordinal) => """
                    {"value":[{"name":"Specs","folder":{"childCount":1},"size":57,"lastModifiedDateTime":"2026-10-29T11:39:11Z"},{"name":"plan.txt","file":{},"size":46}],
                     "@odata.nextLink":"https://graph.microsoft.us/v1.0/drives/b!lab/root:/General:/children?$skiptoken=2"}
                    """,
                "/v1.0/drives/b!lab/root:/General:/children" => """{"value":[{"name":"older.txt","file":{},"size":3}],"@odata.nextLink":"https://evil.example/v1.0/drives/b!lab/root:/General:/children?$skiptoken=3"}""",
                "/v1.0/drives/b!lab/root:/General/plan.txt" => """{"name":"plan.txt","file":{},"size":46,"@microsoft.graph.downloadUrl":"https://t.sharepoint.com/sites/lab/_layouts/15/download.aspx?UniqueId=1"}""",
                "/v1.0/sites/t.sharepoint.com:/sites/Mark8:/drive" => """{"id":"b!mark8"}""",
                "/v1.0/drives/b!mark8/root/children" => """{"value":[{"name":"Plan.docx","file":{},"size":8}]}""",
                "/v1.0/groups/g-lab/drive" => """{"id":"b!lab","webUrl":"https://t.sharepoint.com/sites/lab/Shared%20Documents","sharePointIds":{"siteUrl":"https://t.sharepoint.com/sites/lab"}}""",
                "/v1.0/search/query" when body?.Contains("\"from\":0", StringComparison.Ordinal) == true => """
                    {"value":[{"hitsContainers":[{"hits":[
                      {"resource":{"@odata.type":"#microsoft.graph.site","displayName":"AnyFS Lab","webUrl":"https://t.sharepoint.com/sites/lab","createdDateTime":"2026-10-29T11:28:31Z"}},
                      {"resource":{"@odata.type":"#microsoft.graph.site","displayName":"Mark 8 Project Team","webUrl":"https://t.sharepoint.com/sites/Mark8","description":"The Mark 8"}}
                    ],"total":3,"moreResultsAvailable":true}]}]}
                    """,
                "/v1.0/search/query" => """{"value":[{"hitsContainers":[{"hits":[{"resource":{"displayName":"Retail","webUrl":"https://t.sharepoint.com/sites/Retail"}},{"resource":{"displayName":"Elsewhere","webUrl":"https://evil.example/sites/x"}}],"total":3,"moreResultsAvailable":true}]}]}""",
                _ when path.StartsWith("/v1.0/shares/", StringComparison.Ordinal) => """{"name":"report.docx","file":{},"size":8,"@microsoft.graph.downloadUrl":"https://t.sharepoint.com/sites/lab/_layouts/15/download.aspx?UniqueId=2"}""",
                _ => null,
            };
            return new HttpResponseMessage(json is null ? HttpStatusCode.NotFound : HttpStatusCode.OK) { Content = new StringContent(json ?? """{"error":{"code":"itemNotFound"}}""", Encoding.UTF8, "application/json") };
        }
    }

    [TestMethod]
    public async Task WithGraphsTokenAloneTheFilesAreReadThroughGraph()
    {
        var graph = new GraphFiles();
        var token = GraphToken();
        await using var rest = new TeamsRest(new Uri("https://teams.test"), (s, _, _) => Task.FromResult(s == TeamsService.Graph ? token : "core"), concurrency: 2, NullLogger.Instance, graph,
            sharePointHost: () => null, inHand: s => s == TeamsService.Graph ? token : null);
        Assert.IsTrue(rest.GraphReady && !rest.SharePointReady);
        var lab = new TeamInfo("19:lab", "AnyFS Lab", "g-lab", []);
        var none = CancellationToken.None;

        // a channel's folder, from filesFolder; a team with no site has none
        Assert.AreEqual(("https://t.sharepoint.com/sites/lab", "General"), await rest.ChannelFilesAsync(lab, new ChannelInfo("19:general@thread.tacv2", "General", true, false), null, none));
        Assert.IsNull(await rest.ChannelFilesAsync(lab with { GroupId = "g-nosite" }, new ChannelInfo("19:x@thread.tacv2", "General", true, false), null, none));

        // a shared channel's: the folder its thread names, in a site of its own, with nothing asked of Graph
        var asked = graph.Asked.Count;
        var shared = new ThreadDetails("https://t.sharepoint.com/sites/lab-Design", "/sites/lab-Design/Shared Documents/Design", 3, 1, 1, null);
        Assert.AreEqual(("https://t.sharepoint.com/sites/lab-Design", "Design"), await rest.ChannelFilesAsync(lab, new ChannelInfo("19:design@thread.tacv2", "Design", false, false) { Shared = true }, shared, none));
        Assert.AreEqual(asked, graph.Asked.Count);

        // its children, both pages on Graph and not the one off it; the drive filesFolder named, not looked up again
        var files = await rest.FolderAsync("https://t.sharepoint.com/sites/lab", "General", none);
        CollectionAssert.AreEqual(new[] { "Specs", "plan.txt", "older.txt" }, files.Select(f => f.Name).ToArray());
        Assert.IsTrue(files[0].IsFolder);
        Assert.IsFalse(graph.Asked.Any(a => a.Uri.AbsolutePath.Contains("/sites/", StringComparison.Ordinal) && a.Uri.AbsolutePath.Contains("lab", StringComparison.Ordinal)));

        // a file by its path, with Graph's download address; a shared one by its own address
        Assert.AreEqual("https://t.sharepoint.com/sites/lab/_layouts/15/download.aspx?UniqueId=1", (await rest.FileAsync("https://t.sharepoint.com/sites/lab", "General/plan.txt", null, none))!.DownloadUrl);
        const string Shared = "https://t.sharepoint.com/sites/lab/Shared Documents/General/report.docx";
        Assert.AreEqual("report.docx", (await rest.FileAsync("https://t.sharepoint.com/sites/lab", "General/report.docx", Shared, none))!.Name);
        Assert.IsTrue(graph.Asked.Any(a => a.Uri.AbsolutePath == $"/v1.0/shares/{TeamsFiles.ShareId(Shared)}/driveItem"));

        // another site's library: its drive found from its path, once
        await rest.FolderAsync("https://t.sharepoint.com/sites/Mark8", "", none);
        CollectionAssert.AreEqual(new[] { "Plan.docx" }, (await rest.FolderAsync("https://t.sharepoint.com/sites/Mark8", "", none)).Select(f => f.Name).ToArray());
        Assert.AreEqual(1, graph.Asked.Count(a => a.Uri.AbsolutePath.EndsWith("/sites/t.sharepoint.com:/sites/Mark8:/drive", StringComparison.Ordinal)));

        // the group sites: Graph's Microsoft 365 groups, the public ones and those the account is in, each without its site
        var sites = await rest.GroupSitesAsync(none);
        CollectionAssert.AreEquivalent(new[] { "g-lab", "g-mark8", "g-private-in" }, sites.Select(s => s.GroupId).ToArray(), "not a private or hidden group the account is not in");
        Assert.IsTrue(sites.All(s => s.SiteUrl is null));
        Assert.AreEqual("The Mark 8", sites.Single(s => s.GroupId == "g-mark8").Description);
        Assert.IsFalse(graph.Asked.Any(a => a.Uri.AbsolutePath == "/v1.0/search/query"), "no search where Graph lists the groups");

        // the account's groups given as ids alone (as Graph gives them to a token that may not read groups, the sandbox
        // showed): its teams' instead, not a list of ids
        var mine = await rest.MyGroupsAsync([lab], none);
        Assert.AreEqual((GroupsScope.Teams, "AnyFS Lab"), (mine.Scope, mine.Groups.Single().Name));

        // a group's own site, from its group's drive, looked up once
        Assert.AreEqual("https://t.sharepoint.com/sites/lab", await rest.SiteOfGroupAsync("g-lab", none));
        Assert.AreEqual("https://t.sharepoint.com/sites/lab", await rest.SiteOfGroupAsync("g-lab", none));
        Assert.AreEqual(1, graph.Asked.Count(a => a.Uri.AbsolutePath == "/v1.0/groups/g-lab/drive"));

        // Graph's token, to Graph's host alone
        Assert.IsTrue(graph.Asked.All(a => a.Uri.Host == "graph.microsoft.us" && a.Bearer == token), string.Join("\n", graph.Asked.Select(a => a.Uri)));
    }

    [TestMethod]
    public async Task WhereGraphRefusesItsGroupsTheSitesAreSearchedAsFarAsSearchReaches()
    {
        // a Graph token without Group.Read.All (the AnyFS app's in the sandbox): Microsoft Search, whose reach is 1,000 sites
        // and whose paging is reported to say there is more past the end, so it stops there
        var graph = new GraphFiles { ListsGroups = false };
        var token = GraphToken();
        await using var rest = new TeamsRest(new Uri("https://teams.test"), (s, _, _) => Task.FromResult(s == TeamsService.Graph ? token : "core"), concurrency: 2, NullLogger.Instance, graph,
            sharePointHost: () => null, inHand: s => s == TeamsService.Graph ? token : null);

        var sites = await rest.GroupSitesAsync(CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "AnyFS Lab", "Mark 8 Project Team", "Retail" }, sites.Select(s => s.Name).ToArray(), "both pages; none off SharePoint");
        Assert.IsTrue(sites.All(s => s.GroupId is null && s.SiteUrl is not null));
        var searches = graph.Asked.Where(a => a.Uri.AbsolutePath == "/v1.0/search/query").Select(a => a.Body!).ToList();
        Assert.AreEqual(2, searches.Count, "no page from 1,000 on, though the last still says there are more");
        StringAssert.Contains(searches[1], "\"from\":500");

        // nor is every group listed, nor the account's, without it
        Assert.IsNull(await rest.AllGroupsAsync([], sites, CancellationToken.None));
        Assert.AreEqual(GroupsScope.Teams, (await rest.MyGroupsAsync([], CancellationToken.None)).Scope);
    }

    [TestMethod]
    public void ATokenGoesToItsOwnServiceAlone()
    {
        Assert.IsTrue(TeamsRest.MayCarry(TeamsService.SharePoint, "https://t.sharepoint.com/sites/x/_api/v2.0/drive/root"));
        Assert.IsTrue(TeamsRest.MayCarry(TeamsService.SharePoint, "https://t-my.sharepoint.us/personal/x/_api/v2.0/drive/root"));
        Assert.IsFalse(TeamsRest.MayCarry(TeamsService.SharePoint, "https://graph.microsoft.com/v1.0/me"));
        Assert.IsFalse(TeamsRest.MayCarry(TeamsService.SharePoint, "http://t.sharepoint.com/sites/x"), "https only");
        Assert.IsTrue(TeamsRest.MayCarry(TeamsService.Graph, "https://graph.microsoft.us/v1.0/search/query"));
        Assert.IsTrue(TeamsRest.MayCarry(TeamsService.Graph, "https://dod-graph.microsoft.us/v1.0/me/drive"));
        Assert.IsFalse(TeamsRest.MayCarry(TeamsService.Graph, "https://t.sharepoint.com/_api/search/query"));
        Assert.IsFalse(TeamsRest.MayCarry(TeamsService.Graph, "https://graph.microsoft.com.evil.example/v1.0/me"));
        Assert.IsTrue(TeamsRest.MayCarry(TeamsService.ChatService, "https://apac.ng.msg.teams.microsoft.com/v1/users/ME/conversations"));
    }

    [TestMethod]
    public async Task ARefusalIsNotAskedAgainTwiceNorAfterA403()
    {
        // a service that refuses every token: one fresh one is asked for, then the refusal stands
        var refusals = 0;
        await using var always401 = new TeamsRest(new Uri("https://teams.test"), (_, refused, _) =>
        {
            refusals += refused is null ? 0 : 1;
            return Task.FromResult("token");
        }, concurrency: 2, NullLogger.Instance, new Service(_ => HttpStatusCode.Unauthorized));
        var ex = await Assert.ThrowsExactlyAsync<VfsException>(() => always401.StructureAsync(CancellationToken.None));
        Assert.AreEqual(VfsError.AccessDenied, ex.Error);
        Assert.AreEqual(1, refusals, "one fresh token, not a loop");

        // a 403 is the account's permission, not the token: no fresh one
        refusals = 0;
        await using var forbidden = new TeamsRest(new Uri("https://teams.test"), (_, refused, _) =>
        {
            refusals += refused is null ? 0 : 1;
            return Task.FromResult("token");
        }, concurrency: 2, NullLogger.Instance, new Service(_ => HttpStatusCode.Forbidden));
        ex = await Assert.ThrowsExactlyAsync<VfsException>(() => forbidden.StructureAsync(CancellationToken.None));
        Assert.AreEqual(VfsError.AccessDenied, ex.Error);
        Assert.AreEqual(0, refusals);
    }

    // the shapes a GCC High account's call history, activity feed and channel threads have (the work probe, 30-Oct-2026):
    // call-log and activity held as JSON strings (or objects), the feed repeating the mentions
    private const string CallsNewest = """
        {"messages":[
          {"id":"2","messagetype":"Event/Call","composetime":"2026-10-30T02:00:00.000Z","properties":{"call-log":"{\"startTime\":\"2026-10-30T01:00:00.000Z\",\"connectTime\":\"2026-10-30T01:00:05.000Z\",\"endTime\":\"2026-10-30T01:12:05.000Z\",\"callDirection\":\"outgoing\",\"callType\":\"twoParty\",\"callState\":\"accepted\",\"originatorParticipant\":{\"id\":\"8:orgid:me\",\"displayName\":\"Tester\"},\"targetParticipant\":{\"id\":\"8:orgid:jane\",\"displayName\":\"Jane Doe\"},\"participants\":[\"8:orgid:me\",\"8:orgid:jane\"]}"}},
          {"id":"1","messagetype":"Event/Call","composetime":"2026-10-29T02:00:00.000Z","properties":{"call-log":{"startTime":"2026-10-29T01:00:00.000Z","callDirection":"incoming","callType":"twoParty","callState":"missed","originatorParticipant":{"displayName":"John Roe"},"targetParticipant":{"displayName":"Tester"},"participantList":[{"id":"a"},{"id":"b"},{"id":"c"}]}}}
        ],"_metadata":{"backwardLink":"https://chat.test/v1/users/ME/conversations/48%3Acalllogs/messages?startTime=1&syncState=older"}}
        """;

    private const string CallsOlder = """
        {"messages":[
          {"id":"0","messagetype":"Event/Call","properties":{"call-log":"{\"startTime\":\"2026-10-28T01:00:00.000Z\",\"callDirection\":\"outgoing\",\"callType\":\"group\",\"callState\":\"accepted\"}"}},
          {"id":"x","messagetype":"Text","properties":{"call-log":"not json"}}
        ],"_metadata":{}}
        """;

    private const string Mentions = """
        {"messages":[
          {"id":"7","messagetype":"Text","composetime":"2026-10-30T03:00:00.000Z","properties":{"activity":"{\"activityType\":\"mentions\",\"activitySubtype\":\"mention\",\"activityTimestamp\":\"2026-10-30T03:00:00.000Z\",\"sourceThreadId\":\"19:general\",\"sourceMessageId\":\"1001\",\"sourceUserImDisplayName\":\"Jane Doe\",\"messagePreview\":\"@Tester look\"}"}}
        ],"_metadata":{}}
        """;

    private const string Notifications = """
        {"messages":[
          {"id":"6","messagetype":"Text","composetime":"2026-10-30T03:00:01.000Z","properties":{"activity":"{\"activityType\":\"mentions\",\"activitySubtype\":\"mention\",\"activityTimestamp\":\"2026-10-30T03:00:00.000Z\",\"sourceThreadId\":\"19:general\",\"sourceMessageId\":\"1001\",\"sourceUserImDisplayName\":\"Jane Doe\",\"messagePreview\":\"@Tester look\"}"}},
          {"id":"5","messagetype":"Text","composetime":"2026-10-30T02:30:00.000Z","imdisplayname":"John Roe","properties":{"activity":{"activityType":"reactionInChat","activitySubtype":"like","sourceThreadId":"19:chat","sourceMessageId":"1001"}}},
          {"id":"4","messagetype":"Text","composetime":"2026-10-30T02:00:00.000Z","properties":{"activity":"{\"activityType\":\"msGraph\",\"activitySubtype\":\"privateMeetingCreated\",\"activityTimestamp\":\"2026-10-30T01:59:00.000Z\"}"}}
        ],"_metadata":{}}
        """;

    private const string Thread = """
        {"id":"19:general","type":"Thread","properties":{"sharepointSiteUrl":"https://t.sharepoint.com/sites/anyfslab/","channelDocsFolderRelativeUrl":"/sites/anyfslab/Shared Documents/General"},
         "rosterSummary":{"memberCount":12,"botCount":1,"readerCount":0,"roleCounts":{"User":10,"Admin":2},"externalMemberCount":0,"explicitlyAddedCount":0,"blockedCount":0}}
        """;

    private const string Structure = """
        {"teams":[{"id":"19:team","displayName":"Lab","description":"The lab","smtpAddress":"lab@t.com","classification":"Internal","isFavorite":true,"isTeamLocked":true,"accessType":1,"memberRoleString":"Admin",
          "membershipSummary":{"totalMemberCount":12,"adminRoleCount":2,"botCount":1},
          "channels":[{"id":"19:general","displayName":"General","isGeneral":true,"description":"Everything","creationTime":"2026-10-01T00:00:00Z","creator":"8:orgid:jane","isShared":true,"isFavorite":true,
            "tabs":[{"name":"Wiki","tabType":"website","settings":{"subtype":"webpage","websiteUrl":"https://wiki.t.com/x"}},{"name":"Plan.docx","tabType":"files","settings":{"subtype":"pinnedFile"}},
                    {"name":"Planner","tabType":"extension","settings":{}},{"name":"","tabType":"website"},{"name":"Odd","tabType":"website","settings":{"websiteUrl":"javascript:alert(1)"}}]}]}],
         "chats":[{"id":"19:old","title":"Old","members":[],"hidden":true},{"id":"19:new","title":"New","members":[]}]}
        """;

    /// <summary>The chat service's special conversations and threads, and the aggregator: an account with no 48:mentions where <paramref name="noMentions"/>.</summary>
    private sealed class Feeds(bool noMentions = false) : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var path = Uri.UnescapeDataString(uri.AbsolutePath);
            lock (Asked)
            {
                Asked.Add(path + uri.Query);
            }

            var body = path switch
            {
                "/api/authsvc/v1.0/authz" => Authz,
                "/api/v3/teams/users/me" => Structure,
                "/v1/users/ME/conversations/48:calllogs/messages" when uri.Query.Contains("syncState", StringComparison.Ordinal) => CallsOlder,
                "/v1/users/ME/conversations/48:calllogs/messages" => CallsNewest,
                "/v1/users/ME/conversations/48:mentions/messages" when !noMentions => Mentions,
                "/v1/users/ME/conversations/48:notifications/messages" => Notifications,
                "/v1/threads/19:general" => Thread,
                "/v1/threads/19:odd" => """{"properties":{"sharepointSiteUrl":"https://evil.example/sites/x","channelDocsFolderRelativeUrl":"/sites/x/Shared Documents/Odd"},"rosterSummary":{"memberCount":"3"}}""",
                _ => null,
            };
            return Task.FromResult(new HttpResponseMessage(body is null ? HttpStatusCode.NotFound : HttpStatusCode.OK) { Content = new StringContent(body ?? "{}", Encoding.UTF8, "application/json") });
        }
    }

    [TestMethod]
    public async Task TheCallHistoryIsReadNewestFirstAcrossPagesAndNoFurtherThanAsked()
    {
        var service = new Feeds();
        await using var rest = new TeamsRest(new Uri("https://teams.test"), (_, _, _) => Task.FromResult("token"), concurrency: 2, NullLogger.Instance, service);

        var calls = await rest.CallsAsync(10, CancellationToken.None);
        Assert.IsNotNull(calls);
        Assert.AreEqual(3, calls.Count, "both pages; a message whose call-log is not JSON is left out");
        var made = calls[0];
        Assert.AreEqual(DateTimeOffset.Parse("2026-10-30T01:00:00Z"), made.Start);
        Assert.AreEqual(DateTimeOffset.Parse("2026-10-30T01:12:05Z"), made.End);
        Assert.AreEqual(("outgoing", "twoParty", "accepted", "Jane Doe", 2), (made.Direction, made.Type, made.State, made.Target, made.Participants));
        Assert.AreEqual(("incoming", "missed", "John Roe", 3), (calls[1].Direction, calls[1].State, calls[1].Originator, calls[1].Participants), "a call-log given as an object; participantList counted");
        Assert.AreEqual("group", calls[2].Type);

        // no further back than asked: the first page held enough
        service.Asked.Clear();
        Assert.AreEqual(2, (await rest.CallsAsync(2, CancellationToken.None))!.Count);
        Assert.IsFalse(service.Asked.Any(a => a.Contains("syncState", StringComparison.Ordinal)), "the older page is not read");
    }

    [TestMethod]
    public async Task TheActivityFeedHasEachMentionOnceAndNoFeedsIsNoActivity()
    {
        await using var rest = new TeamsRest(new Uri("https://teams.test"), (_, _, _) => Task.FromResult("token"), concurrency: 2, NullLogger.Instance, new Feeds());
        var items = await rest.ActivityAsync(10, CancellationToken.None);
        Assert.IsNotNull(items);
        Assert.AreEqual(3, items.Count, "the feed's copy of the mention is left out");
        var mention = items[0];
        Assert.AreEqual((true, "mentions", "Jane Doe", "19:general", "@Tester look"), (mention.Mention, mention.Type, mention.From, mention.ThreadId, mention.Preview));
        Assert.AreEqual((false, "reactionInChat", "like", "John Roe"), (items[1].Mention, items[1].Type, items[1].Subtype, items[1].From), "the same message reacted to is not a mention; its sender's name from the message");
        Assert.AreEqual(("privateMeetingCreated", DateTimeOffset.Parse("2026-10-30T01:59:00Z")), (items[2].Subtype, items[2].When));

        // no 48:mentions: the feed's mentions are the mentions
        await using var feedOnly = new TeamsRest(new Uri("https://teams.test"), (_, _, _) => Task.FromResult("token"), concurrency: 2, NullLogger.Instance, new Feeds(noMentions: true));
        var fromFeed = await feedOnly.ActivityAsync(10, CancellationToken.None);
        Assert.AreEqual(1, fromFeed!.Count(i => i.Mention));

        // an account with neither (the sandbox's has no call history either): nothing, not a failure
        await using var none = new TeamsRest(new Uri("https://teams.test"), (_, _, _) => Task.FromResult("token"), concurrency: 2, NullLogger.Instance, new Service(_ => HttpStatusCode.OK));
        Assert.IsNull(await none.ActivityAsync(10, CancellationToken.None));
        Assert.IsNull(await none.CallsAsync(10, CancellationToken.None));
    }

    [TestMethod]
    public async Task AChannelsThreadTellsItsSiteFolderAndRoster()
    {
        await using var rest = new TeamsRest(new Uri("https://teams.test"), (_, _, _) => Task.FromResult("token"), concurrency: 2, NullLogger.Instance, new Feeds());
        var details = await rest.ThreadDetailsAsync("19:general", CancellationToken.None);
        Assert.AreEqual(new ThreadDetails("https://t.sharepoint.com/sites/anyfslab", "/sites/anyfslab/Shared Documents/General", 12, 2, 0, 1), details);

        // a site off SharePoint is not taken; a count given as a string is left out; a thread not answered for is none
        var odd = await rest.ThreadDetailsAsync("19:odd", CancellationToken.None);
        Assert.AreEqual(new ThreadDetails(null, "/sites/x/Shared Documents/Odd", null, null, null, null), odd);
        Assert.IsNull(await rest.ThreadDetailsAsync("19:gone", CancellationToken.None));
    }

    [TestMethod]
    public async Task TheStructureCarriesTheTeamsChannelsTabsAndHiddenChats()
    {
        await using var rest = new TeamsRest(new Uri("https://teams.test"), (_, _, _) => Task.FromResult("token"), concurrency: 2, NullLogger.Instance, new Feeds());
        var structure = await rest.StructureAsync(CancellationToken.None);

        var team = structure.Teams.Single();
        Assert.AreEqual(("The lab", "lab@t.com", "Internal", true, true, false, 1, true), (team.Description, team.Mail, team.Classification, team.Favourite, team.Locked, team.Archived, team.AccessType, team.Owner));
        Assert.AreEqual((12, 2, 1), (team.Members, team.Owners, team.Bots));

        var general = team.Channels.Single();
        Assert.AreEqual(("Everything", DateTimeOffset.Parse("2026-10-01T00:00:00Z"), "8:orgid:jane", true, true), (general.Description, general.Created, general.CreatorMri, general.Shared, general.Favourite));
        CollectionAssert.AreEqual(new[]
        {
            new TabInfo("Wiki", "Website", "https://wiki.t.com/x"),
            new TabInfo("Plan.docx", "Pinned file", null),
            new TabInfo("Planner", "App", null),
            new TabInfo("Odd", "Website", null),
        }, general.Tabs.ToArray(), "one with no name left out; a website's address only where it is a web address");

        CollectionAssert.AreEqual(new[] { ("Old", true), ("New", false) }, structure.Chats.Select(c => (c.Title, c.Hidden)).ToArray());
    }
}
