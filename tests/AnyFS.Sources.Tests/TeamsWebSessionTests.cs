using System.Text;
using AnyFS.Sources.Teams;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnyFS.Sources.Tests;

/// <summary>Which of the page's bearers the Teams web session reads (spec 7.9), with made-up, unsigned tokens; no browser is launched.</summary>
[TestClass]
public class TeamsWebSessionTests
{
    private const string Contoso = "11111111-1111-1111-1111-111111111111";
    private const string Fabrikam = "22222222-2222-2222-2222-222222222222";
    private const string Personal = "9188040d-6c67-4c5b-b112-36a304b66dad";

    private static string B64Url(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Jwt(string aud, string upn, string tid, string id = "1")
        => $"{B64Url("{\"alg\":\"none\"}")}.{B64Url($"{{\"aud\":\"{aud}\",\"upn\":\"{upn}\",\"tid\":\"{tid}\",\"oid\":\"oid-{id}\",\"exp\":{DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()}}}")}.";

    private static TeamsWebSession Session() => new(new Uri("https://teams.test"), "msedge.exe", Path.Combine(Path.GetTempPath(), "anyfs-unused-profile"), "jane@contoso.com", NullLogger.Instance);

    [TestMethod]
    public async Task ASignInNameOtherThanTheEmailIsReadInItsOrganisation()
    {
        var session = Session();
        var chat = Jwt("https://ic3.teams.office.com", "jane.doe@contoso.onmicrosoft.com", Contoso);
        Assert.IsNull(session.Take(chat), "the same organisation under another sign-in name is read");
        Assert.AreEqual(chat, await session.TokenAsync(TeamsService.ChatService, interactive: false, _ => { }, CancellationToken.None));
        Assert.AreEqual("jane.doe@contoso.onmicrosoft.com", session.Account);
    }

    [TestMethod]
    public void AnotherOrganisationOrAPersonalAccountIsNeverRead()
    {
        var session = Session();
        Assert.IsNull(session.Take(Jwt("https://ic3.teams.office.com", "jane@contoso.com", Contoso)));
        StringAssert.Contains(session.Take(Jwt("https://chatsvcagg.teams.microsoft.com", "bob@fabrikam.com", Fabrikam)), "another organisation");

        var personal = Session();
        StringAssert.Contains(personal.Take(Jwt("https://ic3.teams.office.com", "jane@outlook.com", Personal)), "personal Microsoft account");
        Assert.IsNull(personal.Account, "nothing of a personal account is kept");
    }

    [TestMethod]
    public async Task ARefusedTokenIsNotTakenAgainFromThePage()
    {
        var session = Session();
        var old = Jwt("https://ic3.teams.office.com", "jane@contoso.com", Contoso, "old");
        var renewed = Jwt("https://ic3.teams.office.com", "jane@contoso.com", Contoso, "new");
        session.Take(old);
        session.Refused(TeamsService.ChatService, old);
        session.Take(renewed);
        session.Take(old); // the page sends the old one again before it renews it everywhere
        Assert.AreEqual(renewed, await session.TokenAsync(TeamsService.ChatService, interactive: false, _ => { }, CancellationToken.None));
    }
}
