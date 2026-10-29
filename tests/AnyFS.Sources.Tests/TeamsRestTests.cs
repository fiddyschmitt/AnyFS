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
