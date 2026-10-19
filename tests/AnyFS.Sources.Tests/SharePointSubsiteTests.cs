using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;
using AnyFS.Sources.SharePoint;

namespace AnyFS.Sources.Tests;

/// <summary>
/// Subsites in a Subsites folder beside a site's libraries, against an in-memory store (no SharePoint needed): each
/// subsite's own libraries and subsites inside it, its files read and written through its own libraries, and the
/// sites', the Subsites and the libraries' folders left to SharePoint.
/// </summary>
[TestClass]
public class SharePointSubsiteTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [TestMethod]
    public async Task SubsitesAreInASubsitesFolderBesideTheLibraries()
    {
        await using var source = await MemorySharePointSource.StartAsync(MemoryDocumentStore.Sample());
        CollectionAssert.AreEqual(new[] { "Documents", "Site Assets", "Subsites" }, await NamesAsync(source, ""));
        CollectionAssert.AreEqual(new[] { "Foo", "hr" }, await NamesAsync(source, "Subsites"));
        CollectionAssert.AreEqual(new[] { "Documents", "Subsites" }, await NamesAsync(source, "Subsites/Foo"));
        CollectionAssert.AreEqual(new[] { "Bar" }, await NamesAsync(source, "Subsites/Foo/Subsites"));
        CollectionAssert.AreEqual(new[] { "Shared" }, await NamesAsync(source, "Subsites/Foo/Subsites/Bar"), "no Subsites folder where there are none");
        CollectionAssert.AreEqual(new[] { "Documents" }, await NamesAsync(source, "Subsites/hr"));

        var foo = await source.StatAsync("subsites/foo", None);
        Assert.IsTrue(foo is { IsDirectory: true, Name: "Foo" }, "found whatever the case, as SharePoint does, and named as it is");
        Assert.IsTrue(await source.StatAsync("SUBSITES", None) is { IsDirectory: true, Name: "Subsites" });
        Assert.IsTrue((await source.StatAsync("Subsites/Foo/Subsites/Bar/Shared", None))?.IsDirectory);
        Assert.IsNull(await source.StatAsync("Foo", None), "a subsite is in the Subsites folder only");
        Assert.IsNull(await source.StatAsync("Subsites/Nope", None));
        Assert.IsNull(await source.StatAsync("Subsites/Foo/Nope", None));
        Assert.IsNull(await source.StatAsync("Subsites/hr/Subsites", None));
        var ex = await Assert.ThrowsExactlyAsync<VfsException>(() => NamesAsync(source, "Subsites/Foo/Nope"));
        Assert.AreEqual(VfsError.NotFound, ex.Error);
    }

    [TestMethod]
    public async Task ASubsitesFilesGoToItsOwnLibraries()
    {
        var store = MemoryDocumentStore.Sample();
        await using var source = await MemorySharePointSource.StartAsync(store);
        await source.CreateDirectoryAsync("Subsites/Foo/Documents/reports", None);
        await using (var file = await source.OpenAsync("Subsites/Foo/Documents/reports/q3.txt", VfsOpenMode.CreateNew, None))
        {
            await file.WriteAsync(0, "third quarter"u8.ToArray(), None);
        }

        Assert.IsTrue(store.Has("/Foo/Shared Documents", "reports/q3.txt"), "in the subsite's Documents");
        Assert.IsFalse(store.Has("/Shared Documents", "reports/q3.txt"), "not the site's");
        Assert.AreEqual(13, (await source.StatAsync("Subsites/Foo/Documents/reports/q3.txt", None))?.Length);

        await source.RenameAsync("Subsites/Foo/Documents/reports/q3.txt", "Subsites/Foo/Documents/q3.txt", false, None);
        Assert.IsTrue(store.Has("/Foo/Shared Documents", "q3.txt"));
        await using (var file = await source.OpenAsync("Subsites/Foo/Documents/q3.txt", VfsOpenMode.Read, None))
        {
            var back = new byte[13];
            Assert.AreEqual(13, await file.ReadFullyAsync(0, back, None));
            Assert.AreEqual("third quarter", Encoding.UTF8.GetString(back));
        }

        await AssertErrorAsync(VfsError.NotSupported, () => source.RenameAsync("Subsites/Foo/Documents/q3.txt", "Documents/q3.txt", false, None));
        await source.DeleteAsync("Subsites/Foo/Documents/q3.txt", None);
        Assert.IsFalse(store.Has("/Foo/Shared Documents", "q3.txt"));
    }

    [TestMethod]
    public async Task TheSitesAndLibrariesFoldersAreLeftToSharePoint()
    {
        await using var source = await MemorySharePointSource.StartAsync(MemoryDocumentStore.Sample());
        await AssertErrorAsync(VfsError.AlreadyExists, () => source.CreateDirectoryAsync("Subsites", None));
        await AssertErrorAsync(VfsError.AlreadyExists, () => source.CreateDirectoryAsync("Subsites/Foo", None));
        await AssertErrorAsync(VfsError.AlreadyExists, () => source.CreateDirectoryAsync("Subsites/Foo/Documents", None));
        await AssertErrorAsync(VfsError.NotSupported, () => source.CreateDirectoryAsync("Subsites/Foo/New library", None));
        await AssertErrorAsync(VfsError.NotSupported, () => source.CreateDirectoryAsync("Subsites/New subsite", None));
        await AssertErrorAsync(VfsError.NotFound, () => source.CreateDirectoryAsync("Nope/folder", None));
        await AssertErrorAsync(VfsError.InvalidPath, () => source.OpenAsync("Subsites/Foo/loose.txt", VfsOpenMode.Create, None));
        await AssertErrorAsync(VfsError.InvalidPath, () => source.OpenAsync("Subsites/loose.txt", VfsOpenMode.Create, None));
        await AssertErrorAsync(VfsError.NotFound, () => source.OpenAsync("Nope/Documents/x.txt", VfsOpenMode.Create, None));
        await AssertErrorAsync(VfsError.AccessDenied, () => source.DeleteAsync("Subsites", None));
        await AssertErrorAsync(VfsError.AccessDenied, () => source.DeleteAsync("Subsites/Foo", None));
        await AssertErrorAsync(VfsError.AccessDenied, () => source.DeleteAsync("Subsites/Foo/Documents", None));
        await AssertErrorAsync(VfsError.AccessDenied, () => source.RenameAsync("Subsites/Foo", "Subsites/Foo2", false, None));
        await AssertErrorAsync(VfsError.AccessDenied, () => source.RenameAsync("Subsites/Foo/Documents", "Subsites/Foo/Docs", false, None));
        await source.SetTimesAsync("Subsites", null, DateTimeOffset.UtcNow, None);   // kept as SharePoint has them, as for a library's folder
        await source.SetTimesAsync("Subsites/Foo", null, DateTimeOffset.UtcNow, None);
    }

    [TestMethod]
    public async Task ALibraryCalledSubsitesKeepsItsName()
    {
        var store = new MemoryDocumentStore();
        store.AddLibrary("", "/Subsites", "Subsites");
        store.AddSubsite("", "/projects", "projects");
        store.AddLibrary("/projects", "/projects/Shared Documents", "Documents");
        store.Seed("/Subsites", "plan.txt", "p"u8.ToArray());
        await using var source = await MemorySharePointSource.StartAsync(store);
        CollectionAssert.AreEqual(new[] { "Subsites", "Subsites (2)" }, await NamesAsync(source, ""));
        CollectionAssert.AreEqual(new[] { "plan.txt" }, await NamesAsync(source, "Subsites"));
        CollectionAssert.AreEqual(new[] { "projects" }, await NamesAsync(source, "Subsites (2)"));
        CollectionAssert.AreEqual(new[] { "Documents" }, await NamesAsync(source, "Subsites (2)/projects"));
    }

    [TestMethod]
    public async Task ASubsiteIsListedWhenFirstNeededNotForEveryPath()
    {
        var store = MemoryDocumentStore.Sample();
        store.Seed("/Foo/Bar/Shared", "deep/file.txt", "x"u8.ToArray());
        await using var source = await MemorySharePointSource.StartAsync(store);
        Assert.AreEqual(0, store.Loads("/Foo"), "starting lists the site, not its subsites");

        for (var i = 0; i < 3; i++)
        {
            Assert.IsNotNull(await source.StatAsync("Subsites/Foo/Subsites/Bar/Shared/deep/file.txt", None));
        }

        Assert.AreEqual(1, store.Loads("/Foo"), "listed once, on the way down, and known since");
        Assert.AreEqual(1, store.Loads("/Foo/Bar"));
        Assert.AreEqual(0, store.Loads("/hr"));
    }

    [TestMethod]
    public async Task WithALibraryPinnedNoSubsiteIsAskedFor()
    {
        var store = MemoryDocumentStore.Sample();
        store.Seed("/Shared Documents", "a.txt", "a"u8.ToArray());
        await using var source = await MemorySharePointSource.StartAsync(store, pinned: "Documents");
        CollectionAssert.AreEqual(new[] { "a.txt" }, await NamesAsync(source, ""));
        Assert.AreEqual(0, store.SubsiteListings);
    }

    [TestMethod]
    public async Task FreeSpaceIsThatOfTheLibraryThePathIsIn()
    {
        var store = new MemoryDocumentStore();
        store.AddLibrary("", "/Shared Documents", "Documents", quotaTotal: 1000, quotaRemaining: 900);
        store.AddSubsite("", "/Foo", "Foo");
        store.AddLibrary("/Foo", "/Foo/Shared Documents", "Documents", quotaTotal: 100, quotaRemaining: 40);
        await using var source = await MemorySharePointSource.StartAsync(store);
        await NamesAsync(source, "Subsites/Foo");
        Assert.AreEqual(new VfsSpace(100, 40), await source.GetSpaceAsync("Subsites/Foo/Documents/x", None));
        Assert.AreEqual(new VfsSpace(100, 40), await source.GetSpaceAsync("Subsites/Foo", None), "a subsite's folder: its first library");
        Assert.AreEqual(new VfsSpace(1000, 900), await source.GetSpaceAsync("Subsites", None));
        Assert.AreEqual(new VfsSpace(1000, 900), await source.GetSpaceAsync("", None));
    }

    private static async Task<string[]> NamesAsync(ISource source, string path)
    {
        var names = new List<string>();
        await foreach (var entry in source.ListAsync(path, None))
        {
            names.Add(entry.Name);
        }

        return [.. names];
    }

    private static async Task AssertErrorAsync(VfsError expected, Func<Task> action)
    {
        var ex = await Assert.ThrowsExactlyAsync<VfsException>(action);
        Assert.AreEqual(expected, ex.Error, ex.Message);
    }
}

/// <summary>The SDK's contract inside a library of a subsite's subsite, against the in-memory store.</summary>
[TestClass]
public class SharePointSubsiteContractTests : SourceContractTests
{
    private static readonly byte[] Readme = Encoding.UTF8.GetBytes("hello readme from a subsite of a subsite\n");

    protected override async Task<SourceContractSetup> SetUpAsync()
    {
        var store = MemoryDocumentStore.Sample();
        store.Seed("/Foo/Bar/Shared", "fixture/readme.txt", Readme);
        store.Seed("/Foo/Bar/Shared", "fixture/scratch", null);
        var source = await MemorySharePointSource.StartAsync(store);
        const string fixture = "Subsites/Foo/Subsites/Bar/Shared/fixture";
        return new SourceContractSetup(source, fixture, fixture + "/readme.txt", Readme, WritableDirectory: fixture + "/scratch");
    }
}

/// <summary>The SharePoint source over a <see cref="MemoryDocumentStore"/>, with a test context of its own.</summary>
internal sealed class MemorySharePointSource(MemoryDocumentStore store, string? pinned) : SharePointSource
{
    private readonly TestSourceContext _context = new("sharepoint");

    protected override string? PinnedLibrary => pinned;

    protected override int Connections => 2;

    protected override long SpoolBelowBytes => 1 << 20;

    protected override string Target => store.Description;

    public static async Task<MemorySharePointSource> StartAsync(MemoryDocumentStore store, string? pinned = null)
    {
        var source = new MemorySharePointSource(store, pinned);
        await source.InitialiseAsync(source._context, CancellationToken.None);
        return source;
    }

    private protected override Task<IDocumentStore> ConnectAsync(CancellationToken ct) => Task.FromResult<IDocumentStore>(store);

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        _context.Dispose();
    }
}

/// <summary>
/// A site in memory for the source's tests: libraries and subsites by web (the site itself is ""), items by library and
/// path. It checks that every call names a library together with the subsite it is in, as the REST store needs, and
/// counts how often each site is listed.
/// </summary>
internal sealed class MemoryDocumentStore : IDocumentStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<LibraryInfo>> _libraries = new() { [""] = [] };
    private readonly Dictionary<string, List<WebInfo>> _subsites = new() { [""] = [] };
    private readonly Dictionary<string, string> _webOfLibrary = [];
    private readonly Dictionary<(string Library, string Path), Item> _items = [];
    private readonly ConcurrentDictionary<string, int> _loads = new();
    private int _subsiteListings;

    public string Description => "the memory site";

    public string LibraryNoun => "document library";

    /// <summary>How often a site's libraries were listed.</summary>
    public int Loads(string web) => _loads.GetValueOrDefault(web);

    public int SubsiteListings => Volatile.Read(ref _subsiteListings);

    /// <summary>Libraries Documents and Site Assets; subsites Foo (Documents, and a subsite Bar with Shared) and hr (Documents).</summary>
    public static MemoryDocumentStore Sample()
    {
        var store = new MemoryDocumentStore();
        store.AddLibrary("", "/Shared Documents", "Documents");
        store.AddLibrary("", "/SiteAssets", "Site Assets");
        store.AddSubsite("", "/Foo", "Foo");
        store.AddLibrary("/Foo", "/Foo/Shared Documents", "Documents");
        store.AddSubsite("/Foo", "/Foo/Bar", "Bar");
        store.AddLibrary("/Foo/Bar", "/Foo/Bar/Shared", "Shared");
        store.AddSubsite("", "/hr", "hr");
        store.AddLibrary("/hr", "/hr/Shared Documents", "Documents");
        return store;
    }

    public void AddLibrary(string web, string key, string name, long? quotaTotal = null, long? quotaRemaining = null)
    {
        lock (_gate)
        {
            _libraries[web].Add(new LibraryInfo(key, name, quotaTotal, quotaRemaining));
            _webOfLibrary[key] = web;
        }
    }

    public void AddSubsite(string parent, string key, string name)
    {
        lock (_gate)
        {
            _subsites[parent].Add(new WebInfo(key, name));
            _libraries[key] = [];
            _subsites[key] = [];
        }
    }

    /// <summary>A file, or a folder for no contents, with the folders above it.</summary>
    public void Seed(string library, string path, byte[]? contents)
    {
        lock (_gate)
        {
            var parent = VfsPath.Parent(path);
            if (parent.Length > 0 && !_items.ContainsKey((library, parent)))
            {
                Seed(library, parent, null);
            }

            _items[(library, path)] = new Item(contents is null, contents ?? [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1);
        }
    }

    public bool Has(string library, string path)
    {
        lock (_gate)
        {
            return _items.ContainsKey((library, path));
        }
    }

    public void Dispose()
    {
    }

    public Task<IReadOnlyList<LibraryInfo>> ListLibrariesAsync(WebInfo? web, CancellationToken ct)
    {
        var key = web?.Key ?? "";
        _loads.AddOrUpdate(key, 1, (_, n) => n + 1);
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<LibraryInfo>>(_libraries[key].Select(l => l with { Web = web }).ToList());
        }
    }

    public Task<IReadOnlyList<WebInfo>> ListSubsitesAsync(WebInfo? web, CancellationToken ct)
    {
        Interlocked.Increment(ref _subsiteListings);
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<WebInfo>>(_subsites[web?.Key ?? ""].ToList());
        }
    }

    public async IAsyncEnumerable<(VfsEntry Entry, ItemRef Item)> ListChildrenAsync(LibraryInfo library, string itemPath, [EnumeratorCancellation] CancellationToken ct)
    {
        List<(VfsEntry, ItemRef)> children;
        lock (_gate)
        {
            Check(library);
            if (itemPath.Length > 0 && !(_items.TryGetValue((library.Key, itemPath), out var self) && self.IsFolder))
            {
                throw VfsException.NotFound(itemPath);
            }

            children = _items.Where(kv => kv.Key.Library == library.Key && VfsPath.Parent(kv.Key.Path) == itemPath).Select(kv => Describe(library, kv.Key.Path, kv.Value)).ToList();
        }

        await Task.Yield();
        foreach (var child in children)
        {
            yield return child;
        }
    }

    public Task<ItemRef?> GetItemAsync(LibraryInfo library, string itemPath, CancellationToken ct)
    {
        lock (_gate)
        {
            Check(library);
            if (itemPath.Length == 0)
            {
                return Task.FromResult<ItemRef?>(new ItemRef(library.Key, null, null, 0, true));
            }

            return Task.FromResult(_items.TryGetValue((library.Key, itemPath), out var item) ? Describe(library, itemPath, item).Item : null);
        }
    }

    public Task<HttpResponseMessage> OpenContentAsync(LibraryInfo library, string itemPath, ItemRef item, long offset, CancellationToken ct)
    {
        byte[] data;
        lock (_gate)
        {
            Check(library);
            data = _items.TryGetValue((library.Key, itemPath), out var found) && !found.IsFolder ? found.Data : throw VfsException.NotFound(itemPath);
        }

        return Task.FromResult(offset > data.Length
            ? new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable) { Content = new ByteArrayContent([]) }
            : new HttpResponseMessage(offset == 0 ? HttpStatusCode.OK : HttpStatusCode.PartialContent) { Content = new ByteArrayContent(data, (int)offset, data.Length - (int)offset) });
    }

    public async Task<ItemRef> UploadAsync(LibraryInfo library, string itemPath, string localPath, long length, CancellationToken ct)
    {
        var data = await File.ReadAllBytesAsync(localPath, ct);
        lock (_gate)
        {
            Check(library);
            RequireParent(library, itemPath);
            var old = _items.GetValueOrDefault((library.Key, itemPath));
            var item = new Item(false, data, old?.Created ?? DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, (old?.Version ?? 0) + 1);
            _items[(library.Key, itemPath)] = item;
            return Describe(library, itemPath, item).Item;
        }
    }

    public Task<ItemRef> CreateFolderAsync(LibraryInfo library, string itemPath, CancellationToken ct)
    {
        lock (_gate)
        {
            Check(library);
            if (_items.ContainsKey((library.Key, itemPath)))
            {
                throw VfsException.AlreadyExists(itemPath);
            }

            RequireParent(library, itemPath);
            var item = new Item(true, [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1);
            _items[(library.Key, itemPath)] = item;
            return Task.FromResult(Describe(library, itemPath, item).Item);
        }
    }

    public Task DeleteAsync(LibraryInfo library, string itemPath, bool isFolder, CancellationToken ct)
    {
        lock (_gate)
        {
            Check(library);
            if (!_items.Remove((library.Key, itemPath)))
            {
                throw VfsException.NotFound(itemPath);
            }

            foreach (var key in _items.Keys.Where(k => k.Library == library.Key && VfsPath.IsUnder(k.Path, itemPath)).ToList())
            {
                _items.Remove(key);
            }
        }

        return Task.CompletedTask;
    }

    public Task<ItemRef> MoveAsync(LibraryInfo library, string fromPath, string toPath, bool isFolder, CancellationToken ct)
    {
        lock (_gate)
        {
            Check(library);
            if (_items.ContainsKey((library.Key, toPath)))
            {
                throw VfsException.AlreadyExists(toPath);
            }

            RequireParent(library, toPath);
            var moving = _items.Where(kv => kv.Key.Library == library.Key && (kv.Key.Path == fromPath || VfsPath.IsUnder(kv.Key.Path, fromPath))).ToList();
            if (moving.Count == 0)
            {
                throw VfsException.NotFound(fromPath);
            }

            foreach (var (key, item) in moving)
            {
                _items.Remove(key);
                _items[(library.Key, toPath + key.Path[fromPath.Length..])] = item;
            }

            return Task.FromResult(Describe(library, toPath, _items[(library.Key, toPath)]).Item);
        }
    }

    public Task SetTimesAsync(LibraryInfo library, string itemPath, DateTimeOffset? created, DateTimeOffset? modified, CancellationToken ct)
    {
        lock (_gate)
        {
            Check(library);
            var item = _items.TryGetValue((library.Key, itemPath), out var found) ? found : throw VfsException.NotFound(itemPath);
            _items[(library.Key, itemPath)] = item with { Created = created ?? item.Created, Modified = modified ?? item.Modified };
        }

        return Task.CompletedTask;
    }

    /// <summary>The library must come with the subsite it belongs to: the REST store addresses it through that subsite.</summary>
    private void Check(LibraryInfo library)
    {
        if (!_webOfLibrary.TryGetValue(library.Key, out var web) || web != (library.Web?.Key ?? ""))
        {
            throw new InvalidOperationException($"{library.Key} was asked for through site '{library.Web?.Key ?? ""}'");
        }
    }

    private void RequireParent(LibraryInfo library, string itemPath)
    {
        var parent = VfsPath.Parent(itemPath);
        if (parent.Length > 0 && !(_items.TryGetValue((library.Key, parent), out var folder) && folder.IsFolder))
        {
            throw VfsException.NotFound(parent);
        }
    }

    private static (VfsEntry Entry, ItemRef Item) Describe(LibraryInfo library, string path, Item item)
    {
        var name = VfsPath.SplitLast(path).Name;
        var version = item.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var entry = item.IsFolder ? VfsEntry.Directory(name, item.Modified, item.Created) : VfsEntry.File(name, item.Data.Length, item.Modified, item.Created, etag: version);
        return (entry, new ItemRef($"{library.Key}|{path}", version, null, item.Data.Length, item.IsFolder));
    }

    private sealed record Item(bool IsFolder, byte[] Data, DateTimeOffset Created, DateTimeOffset Modified, int Version);
}
