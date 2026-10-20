using System.Text;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;
using AnyFS.Sources.SharePoint;

namespace AnyFS.Sources.Tests;

/// <summary>A source given several sites: each a top-level folder with its own libraries and Subsites, against the in-memory store.</summary>
[TestClass]
public class SharePointSeveralSitesTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>The tenant of <see cref="SharePointOtherSitesTests.Tenant"/>, given /sites/Axle and /teams/Ops as the source's sites.</summary>
    internal static MemoryDocumentStore TwoSites()
    {
        var store = SharePointOtherSitesTests.Tenant();
        store.Sites = [new WebInfo("/sites/Axle", "Axle", "https://contoso.sharepoint.com/sites/Axle"), new WebInfo("/teams/Ops", "Ops", "https://contoso.sharepoint.com/teams/Ops")];
        return store;
    }

    [TestMethod]
    public async Task EachSiteIsAFolderWithItsLibrariesAndSubsites()
    {
        var store = TwoSites();
        await using var source = await MemorySharePointSource.StartAsync(store);
        CollectionAssert.AreEqual(new[] { "Axle", "Ops" }, await SpsTestServer.NamesAsync(source, ""));
        CollectionAssert.AreEqual(new[] { "Documents", "Subsites" }, await SpsTestServer.NamesAsync(source, "Axle"));
        CollectionAssert.AreEqual(new[] { "Documents" }, await SpsTestServer.NamesAsync(source, "Axle/Subsites/Plan"));
        CollectionAssert.AreEqual(new[] { "Documents" }, await SpsTestServer.NamesAsync(source, "ops"));
        Assert.IsTrue(await source.StatAsync("axle", None) is { IsDirectory: true, Name: "Axle" });

        await using (var file = await source.OpenAsync("Axle/Documents/plan.txt", VfsOpenMode.CreateNew, None))
        {
            await file.WriteAsync(0, "axle"u8.ToArray(), None);
        }

        Assert.IsTrue(store.Has("/sites/Axle/Shared Documents", "plan.txt"), "in the site's own Documents");
        Assert.AreEqual(4, (await source.StatAsync("Axle/Documents/plan.txt", None))?.Length);
        Assert.AreEqual(0, store.SiteSearches, "neither is a tenant's root site");
        Assert.AreEqual(0, store.Loads(""), "the tenant's root site, not given, is never listed");
    }

    [TestMethod]
    public async Task TheSitesAreTheSourcesToChoose()
    {
        await using var source = await MemorySharePointSource.StartAsync(TwoSites());
        var ex = await Assert.ThrowsExactlyAsync<VfsException>(() => source.CreateDirectoryAsync("New site", None));
        Assert.AreEqual(VfsError.NotSupported, ex.Error);
        StringAssert.Contains(ex.Message, "creating a site");
        Assert.AreEqual(VfsError.AlreadyExists, (await Assert.ThrowsExactlyAsync<VfsException>(() => source.CreateDirectoryAsync("Axle", None))).Error);
        Assert.AreEqual(VfsError.AccessDenied, (await Assert.ThrowsExactlyAsync<VfsException>(() => source.DeleteAsync("Axle", None))).Error);
        Assert.AreEqual(VfsError.AccessDenied, (await Assert.ThrowsExactlyAsync<VfsException>(() => source.RenameAsync("Axle", "Axel", false, None))).Error);
        Assert.AreEqual(VfsError.InvalidPath, (await Assert.ThrowsExactlyAsync<VfsException>(() => source.OpenAsync("loose.txt", VfsOpenMode.Create, None))).Error);
        Assert.IsNull(await source.StatAsync("Documents", None), "nothing of the tenant's root site shows");
    }

    [TestMethod]
    public async Task ARootSiteAmongThemShowsTheOtherSites()
    {
        var store = SharePointOtherSitesTests.Tenant();
        store.Sites = [new WebInfo("", "contoso.sharepoint.com", "https://contoso.sharepoint.com"), new WebInfo("/sites/Axle", "Axle", "https://contoso.sharepoint.com/sites/Axle")];
        await using var source = await MemorySharePointSource.StartAsync(store);
        CollectionAssert.AreEqual(new[] { "contoso.sharepoint.com", "Axle" }, await SpsTestServer.NamesAsync(source, ""));
        CollectionAssert.AreEqual(new[] { "Documents", "Site Assets", "Subsites", "Sites" }, await SpsTestServer.NamesAsync(source, "contoso.sharepoint.com"));
        CollectionAssert.AreEqual(new[] { "Axle", "Ops (sites)", "Ops (teams)" }, await SpsTestServer.NamesAsync(source, "contoso.sharepoint.com/Sites"));
        CollectionAssert.DoesNotContain(await SpsTestServer.NamesAsync(source, "Axle"), "Sites", "only a root site shows them");
    }

    [TestMethod]
    public void ListedSitesAreNamedAsTheirAddressesEnd()
    {
        static List<string> Names(params string[] urls) => WebInfo.Listed(urls.Select(u => (u, new Uri(u)))).Select(w => w.Name).ToList();
        CollectionAssert.AreEqual(new[] { "Axle", "acme.sharepoint.us", "Plan" }, Names("https://acme.sharepoint.us/sites/Axle", "https://acme.sharepoint.us", "https://acme.sharepoint.us/sites/Axle/Plan"));
        CollectionAssert.AreEqual(new[] { "Ops (sites)", "Ops (teams)" }, Names("https://contoso.sharepoint.com/sites/Ops", "https://contoso.sharepoint.com/teams/Ops"));
        CollectionAssert.AreEqual(new[] { "anyfs (192.168.0.92)", "anyfs (192.168.0.90)" }, Names("http://192.168.0.92/sites/anyfs", "http://192.168.0.90/sites/anyfs"), "the same path on two servers");
        CollectionAssert.AreEqual(new[] { "Team" }, Names("http://intranet/Team"));
        Assert.IsTrue(WebInfo.Listed([("k", new Uri("https://acme.sharepoint.us/"))])[0].IsRoot);
        Assert.IsFalse(WebInfo.Listed([("k", new Uri("https://acme.sharepoint.us/sites/x"))])[0].IsRoot);
    }
}

/// <summary>The SDK's contract inside a library of one of the sites a source was given.</summary>
[TestClass]
public class SharePointSeveralSitesContractTests : SourceContractTests
{
    private static readonly byte[] Readme = Encoding.UTF8.GetBytes("hello readme from one of several sites\n");

    protected override async Task<SourceContractSetup> SetUpAsync()
    {
        var store = SharePointSeveralSitesTests.TwoSites();
        store.Seed("/teams/Ops/Shared Documents", "fixture/readme.txt", Readme);
        store.Seed("/teams/Ops/Shared Documents", "fixture/scratch", null);
        var source = await MemorySharePointSource.StartAsync(store);
        const string fixture = "Ops/Documents/fixture";
        return new SourceContractSetup(source, fixture, fixture + "/readme.txt", Readme, WritableDirectory: fixture + "/scratch");
    }
}

/// <summary>The settings of a source of several sites: the lines, what they are, their name, what is refused.</summary>
[TestClass]
public class SharePointSeveralSitesSettingsTests
{
    [TestMethod]
    public void TheLinesAreTheSites()
    {
        var settings = new SharePointSettings { SiteUrl = "  https://acme.sharepoint.us/sites/Foo \r\n\n https://acme.sharepoint.us/Bar\nhttps://ACME.sharepoint.us/sites/Foo/\n" };
        CollectionAssert.AreEqual(new[] { "https://acme.sharepoint.us/sites/Foo", "https://acme.sharepoint.us/Bar", "https://ACME.sharepoint.us/sites/Foo/" }, settings.SiteLines.ToList());
        CollectionAssert.AreEqual(new[] { "https://acme.sharepoint.us/sites/Foo", "https://acme.sharepoint.us/Bar" }, settings.Sites.Select(s => s.Url).ToList(), "the same site once");
        Assert.IsTrue(settings.IsMultiSite);
        Assert.AreEqual(SharePointKind.Online, settings.Kind);
        Assert.IsFalse(settings.IsRootSite, "the Sites folder belongs to a root site given alone, or to its folder");

        settings.SiteUrl = "https://acme.sharepoint.us\n";
        Assert.IsFalse(settings.IsMultiSite);
        Assert.IsTrue(settings.IsRootSite);
    }

    [TestMethod]
    public void SeveralSitesAreNamedByTheServerTheyShare()
    {
        var plugin = new SharePointPlugin();
        Assert.AreEqual("acme.sharepoint.us", plugin.SuggestName(new SharePointSettings { SiteUrl = "https://acme.sharepoint.us/sites/Foo\nhttps://acme.sharepoint.us/Bar" }));
        Assert.AreEqual("intranet", plugin.SuggestName(new SharePointSettings { SiteUrl = "http://intranet:8080/sites/a\nhttp://intranet:8080/sites/b" }));
        Assert.IsNull(plugin.SuggestName(new SharePointSettings { SiteUrl = "http://intranet/sites/a\nhttp://teams.corp/sites/b" }), "no server they share, no name");
        Assert.AreEqual("acme.sharepoint.us Foo", plugin.SuggestName(new SharePointSettings { SiteUrl = "https://acme.sharepoint.us/sites/Foo\n" }), "one site as before");
        Assert.AreEqual("acme.sharepoint.us Foo", plugin.SuggestName(new SharePointSettings { SiteUrl = "https://acme.sharepoint.us/sites/Foo\nhttps://acme.sharepoint.us/sites/Foo/SitePages/Home.aspx" }), "the same site twice is one");
    }

    [TestMethod]
    public void WhatSeveralSitesCannotBe()
    {
        var plugin = new SharePointPlugin();
        string Messages(SharePointSettings s) => string.Join(" | ", plugin.Validate(s).Where(m => m.IsError).Select(m => m.Message));

        // a user name and password, so the answer is the same off Windows, where signing in as yourself is refused
        Assert.AreEqual("", Messages(new SharePointSettings { SiteUrl = "http://intranet/sites/a\nhttp://teams.corp/sites/b", Username = @"CORP\name", Password = "pw" }), "a Windows sign-in reaches several servers");
        StringAssert.Contains(Messages(new SharePointSettings { SiteUrl = "http://intranet/sites/a\nhttps://contoso.sharepoint.com/sites/b" }), "https://contoso.sharepoint.com/sites/b is SharePoint Online");
        StringAssert.Contains(Messages(new SharePointSettings { SiteUrl = "http://intranet/sites/a\n\nftp://intranet/b" }), "ftp://intranet/b: ");
        StringAssert.Contains(Messages(new SharePointSettings { SiteUrl = "http://intranet/sites/a\nhttp://intranet/sites/b", Library = "Documents" }), "one library only with one site");
        StringAssert.Contains(Messages(new SharePointSettings { SiteUrl = "https://contoso.sharepoint.com/sites/a\nhttps://fabrikam.sharepoint.com/sites/b", SignInMethod = SharePointSettings.WebSession }), "one server");
        Assert.AreEqual("", Messages(new SharePointSettings { SiteUrl = "https://contoso.sharepoint.com/sites/a\nhttps://contoso.sharepoint.com/sites/b", SignInMethod = SharePointSettings.WebSession }));
        StringAssert.Contains(Messages(new SharePointSettings { SiteUrl = "https://contoso.sharepoint.com/sites/a\nhttps://contoso.sharepoint.us/sites/b", SignInMethod = SharePointSettings.AppBrowser }), "Microsoft clouds");
        StringAssert.Contains(Messages(new SharePointSettings { SiteUrl = "eeed9b71-0a43-4258-a7be-21d3959d2c19\neeed9b71-0a43-4258-a7be-21d3959d2c1a" }), "one container type");
    }
}
