using AnyFS.Sources.Teams;

namespace AnyFS.Sources.Tests;

/// <summary>Where the Teams source finds files and images (spec 7.9), and so where its tokens may go: <see cref="TeamsFiles"/>.</summary>
[TestClass]
public class TeamsFilesTests
{
    [TestMethod]
    public void ASharedFileIsFoundInItsSitesLibrary()
    {
        Assert.AreEqual(("https://t.sharepoint.com/sites/lab", "General/a b.txt"),
            TeamsFiles.Locate("https://t.sharepoint.com/sites/lab/Shared Documents/General/a b.txt", "https://t.sharepoint.com/sites/lab/"));
        Assert.AreEqual(("https://t-my.sharepoint.com/personal/jane_t_com", "Microsoft Teams Chat Files/x.txt"),
            TeamsFiles.Locate("https://t-my.sharepoint.com/personal/jane_t_com/Documents/Microsoft%20Teams%20Chat%20Files/x.txt", null));
        Assert.AreEqual(("https://t.sharepoint.us/teams/ops", "Ops/plan.docx"),
            TeamsFiles.Locate("https://t.sharepoint.us/teams/ops/Shared Documents/Ops/plan.docx", null), "GCC High's hosts, and the /teams/ path");
        Assert.AreEqual(("https://a.sharepoint-mil.us/sites/x", "General/m.txt"),
            TeamsFiles.Locate("https://a.sharepoint-mil.us/sites/x/Shared Documents/General/m.txt", null), "DoD's");

        // a site the message names that does not hold the address is not believed
        Assert.AreEqual(("https://t.sharepoint.com/sites/lab", "General/a.txt"),
            TeamsFiles.Locate("https://t.sharepoint.com/sites/lab/Shared Documents/General/a.txt", "https://t.sharepoint.com/sites/other/"));
    }

    [TestMethod]
    public void AnAddressOffSharePointIsNeverLocatedSoTheTokenIsNotSentThere()
    {
        Assert.IsNull(TeamsFiles.Locate("https://evil.example/sites/lab/Shared Documents/a.txt", null));
        Assert.IsNull(TeamsFiles.Locate("https://t.sharepoint.com.evil.example/sites/lab/Shared Documents/a.txt", null));
        Assert.IsNull(TeamsFiles.Locate("http://t.sharepoint.com/sites/lab/Shared Documents/a.txt", null), "not over plain http");
        Assert.IsNull(TeamsFiles.Locate("https://t.sharepoint.com/sites/lab", null), "a site with nothing in it");
    }

    [TestMethod]
    public void GraphsSharesIdIsTheAddressInUnpaddedBase64Url()
    {
        // characters whose base64 has '+', '/' and padding: all three made url-safe, and the address comes back whole
        const string Address = "https://t.sharepoint.com/sites/lab/Shared Documents/General/a>>>b???c.txt";
        var id = TeamsFiles.ShareId(Address);
        StringAssert.StartsWith(id, "u!");
        Assert.IsFalse(id.IndexOfAny(['+', '/', '=']) >= 0, id);
        var body = id[2..].Replace('-', '+').Replace('_', '/');
        Assert.AreEqual(Address, System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(body.PadRight(body.Length + (4 - body.Length % 4) % 4, '='))));
    }

    [TestMethod]
    public void ALibrarysSiteIsItsAddressWithoutTheLibrary()
    {
        Assert.AreEqual("https://t.sharepoint.com/sites/lab", TeamsFiles.SiteOfLibrary("https://t.sharepoint.com/sites/lab/Shared%20Documents"));
        Assert.AreEqual("https://t-my.sharepoint.us/personal/jane_t_com", TeamsFiles.SiteOfLibrary("https://t-my.sharepoint.us/personal/jane_t_com/Documents"));
        Assert.IsNull(TeamsFiles.SiteOfLibrary("https://evil.example/sites/lab/Shared%20Documents"));
        Assert.IsNull(TeamsFiles.SiteOfLibrary(null));
        Assert.AreEqual(TeamsFiles.SiteKey("https://t.sharepoint.com/sites/My%20Lab/"), TeamsFiles.SiteKey("https://T.sharepoint.com/sites/my lab"), "one site however each search writes it");
    }

    [TestMethod]
    public void TheMediaServiceIsTeamsOwnHostsAlone()
    {
        Assert.IsTrue(TeamsFiles.IsMediaService("https://as-prod.asyncgw.teams.microsoft.com/v1/objects/0-x/views/imgo", null));
        Assert.IsTrue(TeamsFiles.IsMediaService("https://us-api.asm.skype.com/v1/objects/0-x/views/imgo", null));
        Assert.IsTrue(TeamsFiles.IsMediaService("https://api.ams.gov.teams.microsoft.us/v1/objects/0-x/views/imgo", null));
        Assert.IsTrue(TeamsFiles.IsMediaService("https://api.ams.dod.teams.microsoft.us/v1/objects/0-x/views/imgo", null), "DoD's, by GCC High's shape");
        Assert.IsTrue(TeamsFiles.IsMediaService("https://media.example/v1/objects/0-x", "https://media.example"), "the host the authz call named");
        Assert.IsFalse(TeamsFiles.IsMediaService("https://evil.example/v1/objects/0-x/views/imgo", "https://as-prod.asyncgw.teams.microsoft.com"));
        Assert.IsFalse(TeamsFiles.IsMediaService("https://asyncgw.teams.microsoft.com.evil.example/x", null));
        Assert.IsFalse(TeamsFiles.IsMediaService("http://as-prod.asyncgw.teams.microsoft.com/x", null));
    }

    [TestMethod]
    public void AMessagesInlineImagesAreItsAmsImages()
    {
        const string Html = """
            <p>Two:</p><img itemscope="" itemtype="http://schema.skype.com/AMSImage" src="https://as-prod.asyncgw.teams.microsoft.com/v1/objects/a/views/imgo?x=1&amp;y=2">
            <img itemtype='http://schema.skype.com/AMSImage' src='https://as-prod.asyncgw.teams.microsoft.com/v1/objects/b/views/imgo'>
            <img itemtype="http://schema.skype.com/Emoji" src="https://statics.teams.cdn.office.net/smile.png" alt="🙂">
            <img src="https://public.example/cat.png">
            """;
        CollectionAssert.AreEqual(
            new[] { "https://as-prod.asyncgw.teams.microsoft.com/v1/objects/a/views/imgo?x=1&y=2", "https://as-prod.asyncgw.teams.microsoft.com/v1/objects/b/views/imgo" },
            TeamsFiles.ImagesIn(Html).ToArray());
        Assert.AreEqual(0, TeamsFiles.ImagesIn("<p>no images</p>").Count);
    }

    [TestMethod]
    public void AnImagesTypeComesFromItsBytesThenItsContentType()
    {
        Assert.AreEqual(".png", TeamsFiles.ExtensionOf([0x89, (byte)'P', (byte)'N', (byte)'G', 13, 10], "image/jpeg"));
        Assert.AreEqual(".jpg", TeamsFiles.ExtensionOf([0xFF, 0xD8, 0xFF, 0xE0], null));
        Assert.AreEqual(".gif", TeamsFiles.ExtensionOf("GIF89a"u8, null));
        Assert.AreEqual(".webp", TeamsFiles.ExtensionOf("RIFF\0\0\0\0WEBPVP8 "u8, null));
        Assert.AreEqual(".jpg", TeamsFiles.ExtensionOf([1, 2, 3], "image/jpeg; charset=binary"));
        Assert.AreEqual(".bin", TeamsFiles.ExtensionOf([1, 2, 3], "application/octet-stream"));
    }

    [TestMethod]
    public void TwinAttachmentsAreNumberedBeforeTheExtensionAndTheSharePointHostIsFoundFromOneDrive()
    {
        CollectionAssert.AreEqual(new[] { "report.docx", "report (2).docx", "Report (3).docx", "notes" }, TeamsFiles.Unique(["report.docx", "report.docx", "Report.docx", "notes"]).ToArray());
        Assert.AreEqual("t.sharepoint.com", TeamsFiles.RootHost("t-my.sharepoint.com"));
        Assert.AreEqual("t.sharepoint.us", TeamsFiles.RootHost("t-my.sharepoint.us"));
        Assert.AreEqual("t.sharepoint.com", TeamsFiles.RootHost("t.sharepoint.com"));
    }
}
