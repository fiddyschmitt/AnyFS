using AnyFS.Core.Operations;
using AnyFS.Core.Runtime;
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

    /// <summary>
    /// A rename onto the entry itself in another case (found by the fallback on a case-sensitive source, by the source on a
    /// case-insensitive one) gives it that spelling. Refused as a collision, it cost data: PowerShell's Move-Item -Force
    /// then deletes "the other file" to make room, which is the entry itself (the SMB conditions found it).
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ACaseOnlyRenameGivesTheEntryTheNewSpelling(bool caseInsensitive)
    {
        var src = new MemorySource(caseInsensitive: caseInsensitive).AddFile("Docs/Foo.txt", "foo").AddFile("Docs/Sub/x.txt", "x");
        var a = await Handles.ReadyAsync("a", src);
        var vfs = Handles.Vfs("work", new CollectionMember(a, "files", false));

        await vfs.RenameAsync("files/Docs/Foo.txt", "files/Docs/foo.txt", false, CancellationToken.None);
        CollectionAssert.AreEquivalent(new[] { "foo.txt", "Sub/" }, await Handles.NamesAsync(vfs, "files/Docs"));
        Assert.AreEqual("foo", await Handles.ReadTextAsync(vfs, "files/Docs/foo.txt"));
        await vfs.RenameAsync("files/docs/foo.txt", "files/Docs/FOO.TXT", true, CancellationToken.None);
        CollectionAssert.AreEquivalent(new[] { "FOO.TXT", "Sub/" }, await Handles.NamesAsync(vfs, "files/Docs"), "the source named in another case, replacing");
        await vfs.RenameAsync("files/Docs/Sub", "files/Docs/sub", false, CancellationToken.None);
        CollectionAssert.AreEquivalent(new[] { "FOO.TXT", "sub/" }, await Handles.NamesAsync(vfs, "files/Docs"), "a folder");
        Assert.AreEqual("x", await Handles.ReadTextAsync(vfs, "files/Docs/sub/x.txt"));
    }

    [TestMethod]
    public async Task ARenameOntoAnotherEntryInAnotherCaseIsStillACollision()
    {
        var src = new MemorySource(caseInsensitive: false).AddFile("Docs/dup.txt", "lower").AddFile("Docs/DUP.txt", "upper");
        var a = await Handles.ReadyAsync("a", src);
        var vfs = Handles.Vfs("work", new CollectionMember(a, "files", false));

        await Handles.ExpectAsync(VfsError.AlreadyExists, () => vfs.RenameAsync("files/Docs/dup.txt", "files/Docs/DUP.txt", false, CancellationToken.None));
        Assert.AreEqual("lower", await Handles.ReadTextAsync(vfs, "files/Docs/dup.txt"));
        Assert.AreEqual("upper", await Handles.ReadTextAsync(vfs, "files/Docs/DUP.txt"));
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
        Assert.IsFalse(vfs.CanWrite(""), "the root is a folder of the layout here");
        Assert.IsTrue((await vfs.StatAsync("files", CancellationToken.None))!.Attributes.HasFlag(VfsAttributes.ReadOnly), "a layout folder's entry is read-only");
        Assert.IsFalse(vfs.CanWrite("files"));
        Assert.IsTrue(vfs.CanWrite("files/rw"), "a source's root takes new entries (a mount asks before creating a child)");
        Assert.IsTrue(vfs.CanWrite("files/rw/anything/new.txt"));
        Assert.IsFalse(vfs.CanWrite("files/ro"));
        Assert.IsFalse(vfs.CanWrite("files/ro/x"));
        Assert.IsFalse(vfs.CanWrite("locked"));
        Assert.IsFalse(vfs.CanWrite("locked/x"));
        Assert.IsFalse(vfs.CanWrite("../x"));
        Assert.IsFalse(vfs.ForMount(true).CanWrite("files/rw/x"));
    }

    /// <summary>A source at an empty path is the collection root: the root takes new entries, and only the root's own deletion is refused.</summary>
    [TestMethod]
    public async Task ASourceAtTheRootMakesTheRootWritable()
    {
        var rw = await Handles.ReadyAsync("rw", new MemorySource());
        var vfs = Handles.Vfs("work", new CollectionMember(rw, "", false));
        Assert.IsTrue(vfs.CanWrite(""), "the lab's FUSE and WebDAV rows could not mkdir at the root of such a collection");
        Assert.IsTrue(vfs.CanWrite("new.txt"));
        Assert.IsFalse((await vfs.StatAsync("", CancellationToken.None))!.Attributes.HasFlag(VfsAttributes.ReadOnly), "the root's entry says it is writable (FUSE derives the mode from it)");
        await vfs.CreateDirectoryAsync("made", CancellationToken.None);
        Assert.IsTrue((await vfs.StatAsync("made", CancellationToken.None))!.IsDirectory);
        await Assert.ThrowsExactlyAsync<VfsException>(() => vfs.DeleteAsync("", CancellationToken.None));

        var ro = Handles.Vfs("work", new CollectionMember(rw, "", true));
        Assert.IsFalse(ro.CanWrite(""), "a read-only membership at the root refuses");
        Assert.IsFalse(ro.CanWrite("x"));
    }

    /// <summary>A source whose stats are counted as the Operations tab counts them.</summary>
    private static async Task<(SourceHandle Handle, Func<long> Stats)> CountedAsync(string name, MemorySource source)
    {
        var tracker = new OperationTracker();
        var handle = new SourceHandle(Guid.NewGuid(), name, "memory");
        await source.InitialiseAsync(new TestSourceContext(name), CancellationToken.None);
        handle.Current = new TrackedSource(source, handle, tracker);
        handle.Status = ComponentStatus.ReadyStatus;
        return (handle, () => tracker.Stats().Sources.SelectMany(s => s.Operations).Where(o => o.Kind == OperationKind.Stat).Sum(o => o.Finished));
    }

    /// <summary>
    /// A path below a folder that holds one source is that source's or no one's, and the source is asked for it once.
    /// It was asked for the path's first name and then for the path, at every call: each thing a mount did with a file
    /// cost its source two stats (the review of 21-Nov-2026, P3).
    /// </summary>
    [TestMethod]
    public async Task APathInAFolderOfOneSourceIsAskedOfTheSourceOnce()
    {
        var (files, stats) = await CountedAsync("files", new MemorySource(caseInsensitive: true).AddFile("a/b/c.txt", "c"));
        var vfs = Handles.Vfs("work", new CollectionMember(files, "files", false));

        Assert.AreEqual(1, (await vfs.StatAsync("files/a/b/c.txt", CancellationToken.None))!.Length);
        Assert.AreEqual(1, stats(), "a file three names down");
        Assert.IsNull(await vfs.StatAsync("files/a/b/none.txt", CancellationToken.None));
        Assert.AreEqual(2, stats(), "one that is not there");
        Assert.AreEqual("c", await Handles.ReadTextAsync(vfs, "files/a/b/c.txt"));
        Assert.AreEqual(3, stats(), "an open");
        await Handles.WriteTextAsync(vfs, "files/a/b/new.txt", "new", VfsOpenMode.CreateNew);
        Assert.AreEqual(5, stats(), "a new file: that it is not there yet, and the folder it goes into");
        Assert.IsTrue((await vfs.StatAsync("files/a", CancellationToken.None))!.IsDirectory);
        Assert.AreEqual(6, stats(), "a path of one name, asked once as it was before");
    }

    /// <summary>With two sources in one folder the first that has a path's first name owns what is below it, so each is asked for that name first, as before.</summary>
    [TestMethod]
    public async Task InAFolderOfTwoSourcesEachIsAskedForTheFirstNameFirst()
    {
        var (alpha, alphaStats) = await CountedAsync("alpha", new MemorySource(caseInsensitive: true).AddFile("a/from-alpha.txt", "alpha"));
        var (beta, betaStats) = await CountedAsync("beta", new MemorySource(caseInsensitive: true).AddFile("a/from-beta.txt", "beta").AddFile("b/only-beta.txt", "ob"));
        var vfs = Handles.Vfs("work", new CollectionMember(alpha, "files", false), new CollectionMember(beta, "files", false));

        Assert.IsNull(await vfs.StatAsync("files/a/from-beta.txt", CancellationToken.None), "alpha has 'a', so beta's is hidden with all in it");
        Assert.AreEqual((2L, 0L), (alphaStats(), betaStats()));
        Assert.IsNotNull(await vfs.StatAsync("files/b/only-beta.txt", CancellationToken.None));
        Assert.AreEqual((3L, 2L), (alphaStats(), betaStats()), "alpha has no 'b'; beta has, and the file");
    }

    /// <summary>
    /// On a source that tells names apart by their case, a name typed in another case is found by its folder's
    /// listing; what follows it is then asked for as typed, before any folder further down is listed. The folders on
    /// the way to a file may be large, and each was listed.
    /// </summary>
    [TestMethod]
    public async Task AfterANameFoundInAnotherCaseTheRestIsAskedForAsTyped()
    {
        var source = new MemorySource(caseInsensitive: false).AddFile("Docs/sub/deep/readme.txt", "readme");
        var (files, stats) = await CountedAsync("files", source);
        var vfs = Handles.Vfs("work", new CollectionMember(files, "files", false));

        // the first name in another case
        Assert.AreEqual("readme", await Handles.ReadTextAsync(vfs, "files/docs/sub/deep/readme.txt"));
        Assert.AreEqual(2, stats(), "the path as typed, and the path with its first name as the source has it");
        Assert.AreEqual(1, source.ListCalls, "the one folder whose listing gave the name");

        // a name further in (the listings are kept for two seconds: another collection of the same source)
        var again = Handles.Vfs("again", new CollectionMember(files, "files", false));
        var (asked, listed) = (stats(), source.ListCalls);
        Assert.AreEqual("readme", await Handles.ReadTextAsync(again, "files/Docs/SUB/deep/readme.txt"));
        Assert.AreEqual(2, stats() - asked, "the path as typed, and the path with that name as the source has it");
        Assert.AreEqual(2, source.ListCalls - listed, "the source's own folder, and the one whose listing gave the name");

        // and one that is nowhere, in any case: the folders on the way to where it would be
        var third = Handles.Vfs("third", new CollectionMember(files, "files", false));
        (asked, listed) = (stats(), source.ListCalls);
        Assert.IsNull(await third.StatAsync("files/Docs/sub/deep/none.txt", CancellationToken.None));
        Assert.AreEqual(1, stats() - asked);
        Assert.AreEqual(4, source.ListCalls - listed);
    }
}
