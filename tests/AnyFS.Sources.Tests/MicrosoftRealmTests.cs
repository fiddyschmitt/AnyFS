using System.Net;
using System.Text;
using AnyFS.Sources.Common;

namespace AnyFS.Sources.Tests;

/// <summary>
/// Which Microsoft cloud an organisation is in, by what the identity platform publishes for its domain
/// (<see cref="MicrosoftRealm"/>). The answers' shapes are the sandbox's own (30-Oct-2026) and, for GCC High and DoD,
/// those CISA's ScubaGear maps.
/// </summary>
[TestClass]
public class MicrosoftRealmTests
{
    internal const string Global = """{"tenant_region_scope":"AS","cloud_instance_name":"microsoftonline.com","cloud_graph_host_name":"graph.windows.net","msgraph_host":"graph.microsoft.com"}""";
    internal const string Gcc = """{"tenant_region_scope":"NA","tenant_region_sub_scope":"GCC","cloud_instance_name":"microsoftonline.com","msgraph_host":"graph.microsoft.com"}""";
    internal const string GccHigh = """{"tenant_region_scope":"USGov","tenant_region_sub_scope":"DODCON","cloud_instance_name":"microsoftonline.us","msgraph_host":"graph.microsoft.us"}""";
    internal const string Dod = """{"tenant_region_scope":"USGov","tenant_region_sub_scope":"DOD","cloud_instance_name":"microsoftonline.us","msgraph_host":"dod-graph.microsoft.us"}""";
    private const string NoSuchTenant = """{"error":"invalid_tenant","error_description":"AADSTS90002: Tenant 'nobody.example' not found."}""";

    /// <summary>Answers by host: each host's status and body, or a network failure where none is given.</summary>
    internal sealed class Login(Dictionary<string, (HttpStatusCode Status, string Body)> answers) : HttpMessageHandler
    {
        public List<Uri> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked.Add(request.RequestUri!);
            return answers.TryGetValue(request.RequestUri!.Host, out var answer)
                ? Task.FromResult(new HttpResponseMessage(answer.Status) { Content = new StringContent(answer.Body, Encoding.UTF8, "application/json") })
                : throw new HttpRequestException($"{request.RequestUri.Host} could not be reached");
        }
    }

    internal static Login Answering(string body, HttpStatusCode status = HttpStatusCode.OK) => new(new() { ["login.microsoftonline.com"] = (status, body) });

    [TestMethod]
    public async Task TheCloudComesFromWhatIsPublishedForTheDomain()
    {
        var global = Answering(Global);
        var realm = await MicrosoftRealm.LookupAsync("Jane.Doe@Contoso.COM", global, CancellationToken.None);
        Assert.AreEqual("https://login.microsoftonline.com/contoso.com/v2.0/.well-known/openid-configuration", global.Asked.Single().AbsoluteUri, "the domain alone, in lower case");
        Assert.AreEqual(("AS", "graph.microsoft.com", false, false), (realm!.Region, realm.GraphHost, realm.IsUsGovernment, realm.IsDod));

        var gcc = (await MicrosoftRealm.LookupAsync("jane@agency.gov", Answering(Gcc), CancellationToken.None))!;
        Assert.AreEqual(("GCC", false), (gcc.SubScope, gcc.IsUsGovernment), "GCC (moderate) is the global cloud");
        var high = (await MicrosoftRealm.LookupAsync("jane@contractor.com", Answering(GccHigh), CancellationToken.None))!;
        Assert.AreEqual((true, false), (high.IsUsGovernment, high.IsDod));
        StringAssert.Contains(high.ToString(), "contractor.com: USGov/DODCON, microsoftonline.us");
        var dod = (await MicrosoftRealm.LookupAsync("jane@army.example", Answering(Dod), CancellationToken.None))!;
        Assert.AreEqual((true, true), (dod.IsUsGovernment, dod.IsDod));
    }

    [TestMethod]
    public async Task AnUnknownDomainOrNoAnswerIsNone()
    {
        var unknown = Answering(NoSuchTenant, HttpStatusCode.BadRequest);
        Assert.IsNull(await MicrosoftRealm.LookupAsync("jane@nobody.example", unknown, CancellationToken.None));
        Assert.AreEqual(1, unknown.Asked.Count, "no organisation by that domain is the answer: the other host is not asked");

        Assert.IsNull(await MicrosoftRealm.LookupAsync("jane@contoso.com", Answering("<html>a captive portal</html>"), CancellationToken.None));
        Assert.IsNull(await MicrosoftRealm.LookupAsync("jane@contoso.com", Answering("{}"), CancellationToken.None), "a document that names no cloud");
        Assert.IsNull(await MicrosoftRealm.LookupAsync("not an address", Answering(Global), CancellationToken.None));
        Assert.IsNull(MicrosoftRealm.DomainOf("jane@"));
        Assert.IsNull(MicrosoftRealm.DomainOf("jane@localhost"), "a domain has a dot");
        Assert.IsNull(MicrosoftRealm.DomainOf("jane@contoso.com/evil"));
    }

    [TestMethod]
    public async Task TheOtherCloudsHostIsAskedWhenTheFirstCannotBeReached()
    {
        // a network that reaches only the US Government cloud's sign-in host, which answers for any organisation's domain
        var usOnly = new Login(new() { ["login.microsoftonline.us"] = (HttpStatusCode.OK, GccHigh) });
        var realm = await MicrosoftRealm.LookupAsync("jane@contractor.com", usOnly, CancellationToken.None);
        CollectionAssert.AreEqual(new[] { "login.microsoftonline.com", "login.microsoftonline.us" }, usOnly.Asked.Select(u => u.Host).ToArray());
        Assert.IsTrue(realm!.IsUsGovernment);

        var busy = new Login(new() { ["login.microsoftonline.com"] = (HttpStatusCode.ServiceUnavailable, ""), ["login.microsoftonline.us"] = (HttpStatusCode.OK, Global) });
        Assert.AreEqual("AS", (await MicrosoftRealm.LookupAsync("jane@contoso.com", busy, CancellationToken.None))!.Region, "a busy host's answer is asked of the other");
        Assert.IsNull(await MicrosoftRealm.LookupAsync("jane@contoso.com", new Login([]), CancellationToken.None), "neither reached");
    }
}
