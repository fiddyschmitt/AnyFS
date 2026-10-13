using AnyFS.Core.Stats;
using AnyFS.Core.Status;
using AnyFS.Core.Vfs;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;

namespace AnyFS.Core.Tests;

/// <summary>Spec 4 and 4.1: tree synthesis, overlays, collisions, case handling, counting.</summary>
[TestClass]
public class CollectionVfsTests
{
    [TestMethod]
    public async Task DefaultPathsGiveTypeFolderThenSourceName()
    {
        var files = await Handles.ReadyAsync("aus-files-01", new MemorySource().AddFile("shared/doc.txt", "doc"));
        var ssh = await Handles.ReadyAsync("aus-files-02", new MemorySource().AddFile("home/smith/.bashrc", "rc"));
        var vfs = Handles.Vfs("work", new CollectionMember(files, "files/aus-files-01", false), new CollectionMember(ssh, "sshfs/aus-files-02", false));

        CollectionAssert.AreEqual(new[] { "files/", "sshfs/" }, await Handles.NamesAsync(vfs, ""));
        CollectionAssert.AreEqual(new[] { "aus-files-01/" }, await Handles.NamesAsync(vfs, "files"));
        CollectionAssert.AreEqual(new[] { "shared/" }, await Handles.NamesAsync(vfs, "files/aus-files-01"));
        Assert.AreEqual("doc", await Handles.ReadTextAsync(vfs, "files/aus-files-01/shared/doc.txt"));
        Assert.AreEqual("rc", await Handles.ReadTextAsync(vfs, "sshfs/aus-files-02/home/smith/.bashrc"));

        var stat = await vfs.StatAsync("files", CancellationToken.None);
        Assert.IsTrue(stat!.IsDirectory);
        Assert.AreEqual("files", stat.Name);
        Assert.IsTrue(stat.Attributes.HasFlag(VfsAttributes.ReadOnly), "synthetic folders are read-only");
        Assert.IsNull(await vfs.StatAsync("nope", CancellationToken.None));
        Assert.IsNull(await vfs.StatAsync("files/nope", CancellationToken.None));
        await Handles.ExpectAsync(VfsError.NotFound, () => Handles.NamesAsync(vfs, "nope"));
    }

    [TestMethod]
    public async Task ShortenedPathMergesTwoMirrorsAtFiles()
    {
        var local = await Handles.ReadyAsync("local", new MemorySource().AddFile("c/users/me/a.txt", "a").AddFile("h/x.txt", "x"));
        var shares = await Handles.ReadyAsync("shares", new MemorySource().AddFile("aus-files-01/shared/b.txt", "b"));
        var vfs = Handles.Vfs("work", new CollectionMember(local, "files", false), new CollectionMember(shares, "files", false));

        CollectionAssert.AreEqual(new[] { "files/" }, await Handles.NamesAsync(vfs, ""));
        CollectionAssert.AreEqual(new[] { "c/", "h/", "aus-files-01/" }, await Handles.NamesAsync(vfs, "files"));
        Assert.AreEqual("a", await Handles.ReadTextAsync(vfs, "files/c/users/me/a.txt"));
        Assert.AreEqual("b", await Handles.ReadTextAsync(vfs, "files/aus-files-01/shared/b.txt"));
        Assert.AreEqual(0, local.Notes.Count + shares.Notes.Count, "no duplicate names, so nothing is degraded");
    }

    [TestMethod]
    public async Task DuplicateNamesAtASharedPathAreShadowedAndNoted()
    {
        var alpha = await Handles.ReadyAsync("alpha", new MemorySource().AddFile("c/from-alpha.txt", "alpha"));
        var beta = await Handles.ReadyAsync("beta", new MemorySource().AddFile("c/from-beta.txt", "beta").AddFile("only-beta.txt", "ob"));
        var vfs = Handles.Vfs("work", new CollectionMember(beta, "files", false), new CollectionMember(alpha, "files", false));

        var names = await Handles.NamesAsync(vfs, "files");
        CollectionAssert.AreEqual(new[] { "c/", "only-beta.txt" }, names, "alpha's 'c' wins by name order; beta's 'c' is hidden");
        Assert.AreEqual("alpha", await Handles.ReadTextAsync(vfs, "files/c/from-alpha.txt"));
        Assert.IsNull(await vfs.StatAsync("files/c/from-beta.txt", CancellationToken.None), "the hidden folder's contents are not merged");
        Assert.AreEqual(1, beta.Notes.Count);
        StringAssert.Contains(beta.Notes.Single(), "files/c");
        Assert.AreEqual(ComponentState.Degraded, beta.EffectiveStatus.State);
        Assert.AreEqual(ComponentState.Ready, alpha.EffectiveStatus.State);
    }

    [TestMethod]
    public async Task EmptyPathMakesTheSourceTheRootAndOthersOverlayIt()
    {
        var nas = await Handles.ReadyAsync("nas", new MemorySource().AddFile("photos/1.jpg", "1").AddFile("archive/old.txt", "old"));
        var extra = await Handles.ReadyAsync("extra", new MemorySource().AddFile("new.txt", "new"));
        var vfs = Handles.Vfs("nas", new CollectionMember(nas, "", false), new CollectionMember(extra, "archive", false));

        CollectionAssert.AreEqual(new[] { "archive/", "photos/" }, await Handles.NamesAsync(vfs, ""));
        CollectionAssert.AreEqual(new[] { "new.txt" }, await Handles.NamesAsync(vfs, "archive"), "the synthetic folder shadows the source's own 'archive'");
        Assert.AreEqual("1", await Handles.ReadTextAsync(vfs, "photos/1.jpg"));
        Assert.IsNull(await vfs.StatAsync("archive/old.txt", CancellationToken.None));
        Assert.AreEqual(1, nas.Notes.Count);
        StringAssert.Contains(nas.Notes.Single(), "collection layout");
    }

    [TestMethod]
    public async Task NestedPathsOverlay()
    {
        var files = await Handles.ReadyAsync("files", new MemorySource().AddFile("a.txt", "a").AddFile("archive/inner.txt", "inner"));
        var archive = await Handles.ReadyAsync("archive", new MemorySource().AddFile("2020.zip", "zip"));
        var vfs = Handles.Vfs("work", new CollectionMember(files, "files", false), new CollectionMember(archive, "files/archive", false));

        CollectionAssert.AreEqual(new[] { "archive/", "a.txt" }, await Handles.NamesAsync(vfs, "files"));
        CollectionAssert.AreEqual(new[] { "2020.zip" }, await Handles.NamesAsync(vfs, "files/archive"));
        Assert.AreEqual("zip", await Handles.ReadTextAsync(vfs, "files/archive/2020.zip"));
    }

    [TestMethod]
    public async Task DisabledSourcesVanishWithoutRestartingAnything()
    {
        var a = await Handles.ReadyAsync("a", new MemorySource().AddFile("x", "x"));
        var b = await Handles.ReadyAsync("b", new MemorySource().AddFile("y", "y"));
        var vfs = Handles.Vfs("work", new CollectionMember(a, "files/a", false), new CollectionMember(b, "files/b", false));
        CollectionAssert.AreEqual(new[] { "a/", "b/" }, await Handles.NamesAsync(vfs, "files"));

        vfs.Update([new CollectionMember(a, "files/a", false)]);
        CollectionAssert.AreEqual(new[] { "a/" }, await Handles.NamesAsync(vfs, "files"));
        Assert.IsNull(await vfs.StatAsync("files/b", CancellationToken.None));

        vfs.Update([]);
        CollectionAssert.AreEqual(Array.Empty<string>(), await Handles.NamesAsync(vfs, ""));
    }

    [TestMethod]
    public async Task SyntheticLevelsMatchCaseInsensitively()
    {
        var a = await Handles.ReadyAsync("a", new MemorySource().AddFile("x.txt", "x"));
        var vfs = Handles.Vfs("work", new CollectionMember(a, "Files/A", false));
        Assert.AreEqual("x", await Handles.ReadTextAsync(vfs, "FILES/a/x.txt"));
        Assert.AreEqual("Files", (await vfs.StatAsync("files", CancellationToken.None))!.Name, "the configured spelling is reported");
    }

    [TestMethod]
    public async Task CaseSensitiveSourcesGetAUniqueCaseInsensitiveFallback()
    {
        var src = new MemorySource(caseInsensitive: false)
            .AddFile("Docs/Readme.TXT", "readme")
            .AddFile("Docs/dup.txt", "lower")
            .AddFile("Docs/DUP.txt", "upper");
        var a = await Handles.ReadyAsync("a", src);
        var vfs = Handles.Vfs("work", new CollectionMember(a, "files", false));

        Assert.AreEqual("readme", await Handles.ReadTextAsync(vfs, "files/docs/readme.txt"), "exact miss, unique case-insensitive hit");
        Assert.AreEqual("Readme.TXT", (await vfs.StatAsync("files/docs/README.txt", CancellationToken.None))!.Name);
        Assert.AreEqual("lower", await Handles.ReadTextAsync(vfs, "files/Docs/dup.txt"), "an exact match wins over the ambiguity");
        Assert.IsNull(await vfs.StatAsync("files/Docs/Dup.txt", CancellationToken.None), "two case-insensitive candidates is not a match");
    }

    [TestMethod]
    public async Task UnavailableSourceStillShowsItsFolderButFailsInside()
    {
        var ready = await Handles.ReadyAsync("ready", new MemorySource().AddFile("ok.txt", "ok"));
        var starting = Handles.Starting("slow");
        var vfs = Handles.Vfs("work", new CollectionMember(ready, "files/ready", false), new CollectionMember(starting, "files/slow", false));

        CollectionAssert.AreEqual(new[] { "ready/", "slow/" }, await Handles.NamesAsync(vfs, "files"));
        var slow = await vfs.StatAsync("files/slow", CancellationToken.None);
        Assert.IsTrue(slow!.IsDirectory);
        var ex = await Handles.ExpectAsync(VfsError.Unavailable, () => vfs.StatAsync("files/slow/anything", CancellationToken.None));
        StringAssert.Contains(ex.Message, "slow");
        await Handles.ExpectAsync(VfsError.Unavailable, () => Handles.NamesAsync(vfs, "files/slow"));
    }

    [TestMethod]
    public async Task SyntheticFolderReportsNewestModifiedOfItsSources()
    {
        var older = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var newer = new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var a = await Handles.ReadyAsync("a", new MemorySource().AddFile("x", "x"));
        var b = await Handles.ReadyAsync("b", new MemorySource().AddFile("y", "y"));
        await a.Current!.SetTimesAsync("", null, older, CancellationToken.None);
        await b.Current!.SetTimesAsync("", null, newer, CancellationToken.None);
        var vfs = Handles.Vfs("work", new CollectionMember(a, "files", false), new CollectionMember(b, "files", false));
        var files = await vfs.StatAsync("files", CancellationToken.None);
        Assert.AreEqual(newer, files!.Modified);
        Assert.AreEqual(0, files.Length);
    }

    [TestMethod]
    public async Task WritesInsideASourceAndAtASharedNode()
    {
        var a = await Handles.ReadyAsync("a", new MemorySource().AddDirectory("docs"));
        var b = await Handles.ReadyAsync("b", new MemorySource(canWrite: false).AddFile("ro.txt", "ro"));
        var vfs = Handles.Vfs("work", new CollectionMember(a, "files", false), new CollectionMember(b, "files", false));

        await Handles.WriteTextAsync(vfs, "files/docs/new.txt", "hello", VfsOpenMode.CreateNew);
        Assert.AreEqual("hello", await Handles.ReadTextAsync(vfs, "files/docs/new.txt"));
        await Handles.ExpectAsync(VfsError.AlreadyExists, () => vfs.OpenAsync("files/docs/new.txt", VfsOpenMode.CreateNew, CancellationToken.None));

        await vfs.CreateDirectoryAsync("files/made-here", CancellationToken.None);
        Assert.IsTrue(((MemorySource)a.Current!).Exists("made-here"), "a create at the shared node goes to the first writable source");
        await Handles.ExpectAsync(VfsError.AlreadyExists, () => vfs.CreateDirectoryAsync("files/docs", CancellationToken.None));
        await Handles.ExpectAsync(VfsError.AlreadyExists, () => vfs.CreateDirectoryAsync("files", CancellationToken.None));

        await vfs.RenameAsync("files/docs/new.txt", "files/docs/renamed.txt", false, CancellationToken.None);
        Assert.IsNull(await vfs.StatAsync("files/docs/new.txt", CancellationToken.None));
        await Handles.ExpectAsync(VfsError.NotSupported, () => vfs.RenameAsync("files/docs/renamed.txt", "files/ro.txt", true, CancellationToken.None));
        await Handles.ExpectAsync(VfsError.ReadOnly, () => vfs.DeleteAsync("files", CancellationToken.None));
        await Handles.ExpectAsync(VfsError.ReadOnly, () => vfs.RenameAsync("files", "other", false, CancellationToken.None));
        await Handles.ExpectAsync(VfsError.IsADirectory, () => vfs.OpenAsync("files", VfsOpenMode.Read, CancellationToken.None));
        await Handles.ExpectAsync(VfsError.NotFound, () => vfs.OpenAsync("files/missing/x.txt", VfsOpenMode.Create, CancellationToken.None));

        await vfs.SetTimesAsync("files/docs/renamed.txt", null, new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero), CancellationToken.None);
        await vfs.DeleteAsync("files/docs/renamed.txt", CancellationToken.None);
        await vfs.DeleteAsync("files/docs", CancellationToken.None);
        Assert.IsNull(await vfs.StatAsync("files/docs", CancellationToken.None));
    }

    [TestMethod]
    public async Task StatsCountFilesOncePerOpenAndBytesBothWays()
    {
        var stats = new StatsCollector();
        var a = await Handles.ReadyAsync("a", new MemorySource().AddFile("x.txt", "0123456789"));
        var vfs = Handles.Vfs("work", stats);
        vfs.Update([new CollectionMember(a, "files", false)]);

        await using (var f = await vfs.OpenAsync("files/x.txt", VfsOpenMode.Read, CancellationToken.None))
        {
            var buffer = new byte[4];
            await f.ReadAsync(0, buffer, CancellationToken.None);
            await f.ReadAsync(4, buffer, CancellationToken.None);
            await f.ReadAsync(100, buffer, CancellationToken.None);
        }

        await using (var f = await vfs.OpenAsync("files/x.txt", VfsOpenMode.ReadWrite, CancellationToken.None))
        {
            await f.WriteAsync(10, "abc"u8.ToArray(), CancellationToken.None);
        }

        var snapshot = stats.Snapshot();
        Assert.AreEqual(1, snapshot.Session.FilesServed, "one read handle delivered bytes");
        Assert.AreEqual(8, snapshot.Session.BytesRead);
        Assert.AreEqual(3, snapshot.Session.BytesWritten);
        Assert.AreEqual(8, snapshot.Collections["work"].Session.BytesRead);
    }

    [TestMethod]
    public async Task ListingsFeedTheFilesSeenCount()
    {
        var src = new MemorySource().AddFile("a/1", "1").AddFile("a/2", "2").AddFile("b/3", "3").AddFile("top", "t");
        var a = await Handles.ReadyAsync("a", src);
        var vfs = Handles.Vfs("work", new CollectionMember(a, "files", false));
        await Handles.NamesAsync(vfs, "files");
        Assert.AreEqual((1L, false), a.FileCount());
        await Handles.NamesAsync(vfs, "files/a");
        await Handles.NamesAsync(vfs, "files/b");
        Assert.AreEqual((4L, false), a.FileCount());
        await Handles.NamesAsync(vfs, "files/a");
        Assert.AreEqual((4L, false), a.FileCount(), "relisting does not double count");
        src.Counters.Set(99, 3, EnumerationState.Complete);
        Assert.AreEqual((99L, true), a.FileCount(), "a complete count from the source wins");
    }

    [TestMethod]
    public async Task CanWriteAnswersWithoutIo()
    {
        var rw = await Handles.ReadyAsync("rw", new MemorySource());
        var ro = await Handles.ReadyAsync("ro", new MemorySource(canWrite: false));
        var vfs = Handles.Vfs("work", new CollectionMember(rw, "files/rw", false), new CollectionMember(ro, "files/ro", false), new CollectionMember(rw, "locked", true));
        Assert.IsFalse(vfs.CanWrite(""));
        Assert.IsFalse(vfs.CanWrite("files"));
        Assert.IsTrue(vfs.CanWrite("files/rw/anything/new.txt"));
        Assert.IsFalse(vfs.CanWrite("files/ro/x"));
        Assert.IsFalse(vfs.CanWrite("locked/x"));
        Assert.IsFalse(vfs.CanWrite("../x"));
        Assert.IsFalse(vfs.ForMount(true).CanWrite("files/rw/x"));
    }
}
