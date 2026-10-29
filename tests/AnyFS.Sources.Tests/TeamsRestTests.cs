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
                "/v1.0/me/memberOf/microsoft.graph.group" => """{"value":[{"id":"g-lab"},{"id":"g-sales"}]}""",
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

        var groups = await rest.GroupsAsync(Joined, await rest.GroupSitesAsync(CancellationToken.None), CancellationToken.None);

        Assert.IsTrue(groups.Complete);
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
    }

    [TestMethod]
    public async Task WithoutAGraphTokenTheGroupsSharePointShowsAreListed()
    {
        var directory = new Directory();
        await using var rest = new TeamsRest(new Uri("https://teams.test"), (_, _, _) => Task.FromResult("token"), concurrency: 2, NullLogger.Instance, directory,
            sharePointHost: () => "t-my.sharepoint.com", inHand: s => s == TeamsService.SharePoint ? "token" : null);
        Assert.IsFalse(rest.GraphReady);

        var groups = await rest.GroupsAsync(Joined, await rest.GroupSitesAsync(CancellationToken.None), CancellationToken.None);

        Assert.IsFalse(groups.Complete);
        Assert.IsFalse(groups.JoinedOnly);
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

        var groups = await rest.GroupsAsync(Joined, sites: null, CancellationToken.None);

        Assert.IsTrue(groups.JoinedOnly);
        Assert.AreEqual(("Private", (bool?)true, 4), (groups.Groups.Single(g => g.Id == "g-lab").Visibility, groups.Groups.Single(g => g.Id == "g-lab").Team, groups.Groups.Single(g => g.Id == "g-lab").Members));
        Assert.AreEqual("Org-wide", groups.Groups.Single(g => g.Id == "g-msft").Visibility);
        CollectionAssert.AreEquivalent(new[] { "g-lab" }, groups.Owner.ToArray());
        Assert.AreEqual(0, directory.Asked.Count, "no SharePoint and no Graph: nothing to ask");
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
}
