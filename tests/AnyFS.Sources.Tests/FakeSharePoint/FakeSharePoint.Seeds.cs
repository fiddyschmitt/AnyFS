using System.Text;
using AnyFS.Sources.SharePoint;

namespace AnyFS.Sources.Tests;

/// <summary>What the fakes hold to begin with: a farm as the lab's (provisioning/sharepoint), a tenant as the sandbox.</summary>
internal sealed partial class FakeSharePoint
{
    /// <summary>The lab farms' test account, as the live tests name it.</summary>
    public const string FarmUser = @"ANYFS\sp_user";

    /// <summary>The fake's own password for its accounts (made up here; nothing real).</summary>
    public const string FakePassword = "fake-sharepoint-pw";

    /// <summary>An account that may only read the reader subsite.</summary>
    public const string FarmReader = @"ANYFS\sp_reader";

    /// <summary>The tenant's test account (the sandbox's anyfs-tester).</summary>
    public const string TenantUser = "tester@anyfs-fake.onmicrosoft.com";

    /// <summary>The tenant's administrator.</summary>
    public const string TenantAdmin = "admin@anyfs-fake.onmicrosoft.com";

    /// <summary>The SharePoint Embedded container type the tenant's containers are of.</summary>
    public static readonly Guid ContainerType = new("eeed9b71-0a43-4258-a7be-21d3959d2c19");

    /// <summary>The test site, on the farm and in the tenant.</summary>
    public const string TestSitePath = "/sites/anyfs";

    /// <summary>
    /// A farm as the lab's: the setup account's root site, which the test account may not open (every root call a 401 with
    /// SharePoint's own error); the test site /sites/anyfs, a modern team site where the test account is a site
    /// collection administrator (so file times stick); a classic subsite of the root, /anyfs-reader, the test account
    /// and a reader may read. No search service, as the lab's farms have none.
    /// </summary>
    public static FakeSharePoint LabFarm(string listenHost = "127.0.0.1", FarmSignIn signIn = FarmSignIn.Basic)
    {
        var fake = StartFarm(listenHost, signIn);
        lock (fake._gate)
        {
            fake.AddAccount(FarmUser, FakePassword);
            fake.AddAccount(FarmReader, FakePassword);
            fake.AddAccount(@"ANYFS\sp_setup", FakePassword);
            fake.Root.Title = "AnyFS farm";
            fake.Root.Roles[@"ANYFS\sp_setup"] = Role.Owner;
            fake.Root.Modern = false;
            fake.SeedTeamSite(fake.Root);

            var site = fake.AddCollectionLocked(TestSitePath, "anyfs");
            site.Roles[FarmUser] = Role.Owner;
            site.Roles[@"ANYFS\sp_setup"] = Role.Owner;
            fake.SeedTeamSite(site);

            var reader = new Web(fake, "/anyfs-reader", "AnyFS reader subsite", fake.Root) { Modern = false, UniquePermissions = true };
            fake.Root.Subwebs.Add(reader);
            reader.Roles[FarmUser] = Role.Read;
            reader.Roles[FarmReader] = Role.Read;
            fake.SeedTeamSite(reader);
            fake.Seed(reader.Library("Documents"), "readme.md", Encoding.UTF8.GetBytes("# The reader's subsite\n"));
            fake.WindowsUserAs = FarmUser;
        }

        return fake;
    }

    /// <summary>
    /// A tenant as the sandbox: its root site, open to every member; the test site /sites/anyfs, a modern team site (the
    /// test account its owner, with a library titled "Health / Safety" and the subsite anyfs-subsite made before the
    /// tenant stopped allowing new ones); other site collections for its Sites folder, /sites/Axle, /sites/Ops and
    /// /teams/Ops, and one the test account may not open; SharePoint Embedded containers of <see cref="ContainerType"/>.
    /// The source goes at SharePoint Online's pace only when <paramref name="paced"/> (<see cref="Tenant"/>).
    /// </summary>
    public static FakeSharePoint Sandbox(string label = "anyfs-fake", bool paced = false)
    {
        var fake = Tenant(label, paced);
        lock (fake._gate)
        {
            fake.AddAccount(TenantUser);
            fake.AddAccount(TenantAdmin);
            fake.AddAccount($"outsider@{label}.onmicrosoft.com");
            fake.Root.Title = "Communication site";
            fake.Root.Everyone = Role.Read;
            fake.Root.Roles[TenantAdmin] = Role.Owner;
            fake.SeedTeamSite(fake.Root);

            var site = fake.AddCollectionLocked(TestSitePath, "AnyFS test site");
            site.Roles[TenantUser] = Role.Owner;
            site.Roles[TenantAdmin] = Role.Owner;
            fake.SeedTeamSite(site);
            var health = new Library(fake, site, "Health / Safety", "Health and Safety", 101);
            site.Libraries.Add(health);
            var subsite = new Web(fake, $"{TestSitePath}/{SpsTestServer.Subsite}", "AnyFS subsite", site);
            site.Subwebs.Add(subsite);
            fake.SeedTeamSite(subsite);

            foreach (var (path, title) in new[] { ("/sites/Axle", "Axle"), ("/sites/Ops", "Ops"), ("/teams/Ops", "Ops team") })
            {
                var other = fake.AddCollectionLocked(path, title);
                other.Everyone = Role.Read;
                fake.SeedTeamSite(other);
            }

            var closed = fake.AddCollectionLocked("/sites/Board", "Board");
            closed.Roles[TenantAdmin] = Role.Owner;
            fake.SeedTeamSite(closed);

            fake.SubsitesAllowed = false;
            fake.CustomScript = false;

            foreach (var (name, status, member) in new[] { ("anyfs-test", "active", true), ("archived", "inactive", true), ("someone else's", "active", false) })
            {
                var container = new Library(fake, web: null, name, name, 101) { ContainerTypeId = ContainerType, Status = status };
                container.Members.Add(TenantAdmin);
                if (member)
                {
                    container.Members.Add(TenantUser);
                }

                fake._containers.Add(container);
            }
        }

        return fake;
    }

    /// <summary>The test site of the farm or tenant.</summary>
    public Web TestSite => WebAt(TestSitePath)!;

    /// <summary>
    /// A team site's lists as SharePoint makes them: Documents, Site Assets, Site Pages (its home page), Style Library and
    /// Form Templates, and SharePoint's own hidden ones and galleries the source never shows; and its own folder's
    /// default.aspx, a classic page or the modern site's way to its home page. Graph's drives leave out all but Documents.
    /// </summary>
    private void SeedTeamSite(Web web)
    {
        var documents = new Library(this, web, "Documents", "Shared Documents", 101);
        documents.Root.AddFolder("Forms").AddFile("AllItems.aspx", Encoding.UTF8.GetBytes("<%@ Page %>"));
        web.Libraries.Add(documents);
        foreach (var (title, url) in new[] { ("Site Assets", "SiteAssets"), ("Style Library", "Style Library"), ("Form Templates", "FormServerTemplates") })
        {
            var library = new Library(this, web, title, url, 101) { InGraphDrives = false };
            library.Root.AddFolder("Forms").AddFile("AllItems.aspx", Encoding.UTF8.GetBytes("<%@ Page %>"));
            web.Libraries.Add(library);
        }

        var pages = new Library(this, web, "Site Pages", "SitePages", 119) { InGraphDrives = false };
        web.Libraries.Add(pages);
        var home = pages.Root.AddFile("Home.aspx", Encoding.UTF8.GetBytes("<%@ Page %>"));
        home.Title = "Home";
        home.CanvasContent1 = CanvasHtml([$"<p>Welcome to {System.Net.WebUtility.HtmlEncode(web.Title)}.</p>"]);

        foreach (var (title, url, template) in new[] { ("Master Page Gallery", "_catalogs/masterpage", 116), ("appdata", "_catalogs/appdata", 125), ("TaxonomyHiddenList", "Lists/TaxonomyHiddenList", 100), ("User Information List", "_catalogs/users", 112) })
        {
            web.Libraries.Add(new Library(this, web, title, url, template) { Hidden = template != 116, InGraphDrives = false });
        }

        var landing = web.Modern
            ? "<%@ Page Inherits=\"Microsoft.SharePoint.WebPartPages.WebPartPage\" %>"
            : $"""<%@ Page language="C#" MasterPageFile="~masterurl/default.master" %><asp:Content ContentPlaceHolderId="PlaceHolderPageTitleInTitleArea" runat="server">{web.Title}</asp:Content><asp:Content ContentPlaceHolderId="PlaceHolderMain" runat="server"><div><p>Welcome to {web.Title}.</p></div></asp:Content>""";
        web.OwnFolder.AddFile("default.aspx", Encoding.UTF8.GetBytes(landing));
    }

    /// <summary>What a test gives the source in place of Microsoft 365 and the browser: this fake, signed in to as <paramref name="account"/>.</summary>
    public SharePointStandIn StandIn(string account = TenantUser) => new(
        Handler,
        account,
        (_, refresh) => Task.FromResult(IssueToken(account, refresh)),
        (interactive, _) => Task.FromResult(SignInSession(account, interactive)));
}
