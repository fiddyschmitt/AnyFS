using AnyFS.Sources.Common;
using AnyFS.Sources.Teams;
using static AnyFS.Sources.Tests.MicrosoftRealmTests;

namespace AnyFS.Sources.Tests;

/// <summary>The Teams source's Cloud setting (spec 7.9): Automatic finds the cloud from the account's domain (<see cref="MicrosoftRealm"/>), remembers it, and a choice wins.</summary>
[TestClass]
public class TeamsCloudTests
{
    [TestMethod]
    public async Task EachCloudIsTheTeamsCloudOfItsName()
    {
        Assert.AreSame(TeamsCloud.Global, TeamsCloud.Of((await MicrosoftRealm.LookupAsync("jane@contoso.com", Answering(Global), CancellationToken.None))!));
        Assert.AreSame(TeamsCloud.Global, TeamsCloud.Of((await MicrosoftRealm.LookupAsync("jane@agency.gov", Answering(Gcc), CancellationToken.None))!), "GCC (moderate) is the global cloud's Teams");
        Assert.AreSame(TeamsCloud.UsGov, TeamsCloud.Of((await MicrosoftRealm.LookupAsync("jane@contractor.com", Answering(GccHigh), CancellationToken.None))!));
        Assert.AreSame(TeamsCloud.UsGovDod, TeamsCloud.Of((await MicrosoftRealm.LookupAsync("jane@army.example", Answering(Dod), CancellationToken.None))!));
    }

    [TestMethod]
    public async Task AutomaticIsFoundRememberedAndOverridden()
    {
        var state = Path.Combine(Path.GetTempPath(), $"anyfs-teams-cloud-{Guid.NewGuid():N}");
        try
        {
            var automatic = new TeamsSettings { Account = "jane@contractor.com" };
            Assert.AreEqual(TeamsSettings.AutomaticCloud, automatic.Cloud, "Automatic is the default");

            // found from the domain, and kept
            var (cloud, how) = await TeamsCloud.ResolveAsync(automatic, state, Answering(GccHigh), CancellationToken.None);
            Assert.AreSame(TeamsCloud.UsGov, cloud);
            StringAssert.Contains(how, "found from contractor.com: USGov/DODCON");
            Assert.IsTrue(File.Exists(Path.Combine(state, "cloud.json")));

            // a start that cannot look it up goes by the last answer for that domain
            (cloud, how) = await TeamsCloud.ResolveAsync(automatic, state, new Login([]), CancellationToken.None);
            Assert.AreSame(TeamsCloud.UsGov, cloud);
            StringAssert.Contains(how, "as last found for contractor.com");

            // not another domain's last answer: then the global cloud, and the setting named as the way out
            (cloud, how) = await TeamsCloud.ResolveAsync(new TeamsSettings { Account = "jane@other.example" }, state, new Login([]), CancellationToken.None);
            Assert.AreSame(TeamsCloud.Global, cloud);
            StringAssert.Contains(how, "choose the Cloud setting");

            // a cloud chosen in the settings wins, without a lookup
            var never = new Login([]);
            (cloud, _) = await TeamsCloud.ResolveAsync(new TeamsSettings { Account = "jane@contractor.com", Cloud = "US Gov (DoD)" }, state, never, CancellationToken.None);
            Assert.AreSame(TeamsCloud.UsGovDod, cloud);
            (cloud, _) = await TeamsCloud.ResolveAsync(new TeamsSettings { Account = "jane@contractor.com", Cloud = "Microsoft 365 (global)" }, state, never, CancellationToken.None);
            Assert.AreSame(TeamsCloud.Global, cloud, "a source saved before Automatic keeps the cloud it had");
            Assert.AreEqual(0, never.Asked.Count);
        }
        finally
        {
            if (Directory.Exists(state))
            {
                Directory.Delete(state, recursive: true);
            }
        }
    }
}
