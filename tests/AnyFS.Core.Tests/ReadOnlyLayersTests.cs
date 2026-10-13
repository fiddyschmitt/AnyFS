using AnyFS.Core.Vfs;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;

namespace AnyFS.Core.Tests;

/// <summary>Spec 4.2: a write is allowed only if the source can, the membership allows and the mount allows.</summary>
[TestClass]
public class ReadOnlyLayersTests
{
    private static readonly string[] Ops = ["create", "write", "truncate", "mkdir", "delete", "rename", "times"];

    private static async Task RunAsync(IVirtualFileSystem fs, string op)
    {
        switch (op)
        {
            case "create":
                await using (await fs.OpenAsync("files/new.txt", VfsOpenMode.CreateNew, CancellationToken.None))
                {
                }

                break;
            case "write":
                await using (var f = await fs.OpenAsync("files/existing.txt", VfsOpenMode.ReadWrite, CancellationToken.None))
                {
                    await f.WriteAsync(0, "x"u8.ToArray(), CancellationToken.None);
                }

                break;
            case "truncate":
                await using (await fs.OpenAsync("files/existing.txt", VfsOpenMode.Create, CancellationToken.None))
                {
                }

                break;
            case "mkdir":
                await fs.CreateDirectoryAsync("files/dir", CancellationToken.None);
                break;
            case "delete":
                await fs.DeleteAsync("files/existing.txt", CancellationToken.None);
                break;
            case "rename":
                await fs.RenameAsync("files/existing.txt", "files/moved.txt", false, CancellationToken.None);
                break;
            case "times":
                await fs.SetTimesAsync("files/existing.txt", null, DateTimeOffset.UtcNow, CancellationToken.None);
                break;
        }
    }

    private static async Task<(SourceHandleBundle Bundle, IVirtualFileSystem Fs)> SetupAsync(bool canWrite, bool membershipReadOnly, bool mountReadOnly)
    {
        var source = new MemorySource(canWrite).AddFile("existing.txt", "e");
        var handle = await Handles.ReadyAsync("src", source);
        var vfs = Handles.Vfs("work", new CollectionMember(handle, "files", membershipReadOnly));
        return (new SourceHandleBundle(source), vfs.ForMount(mountReadOnly));
    }

    [TestMethod]
    public async Task EveryWriteIsRefusedByTheMountLayer()
    {
        foreach (var op in Ops)
        {
            var (bundle, fs) = await SetupAsync(canWrite: true, membershipReadOnly: false, mountReadOnly: true);
            var ex = await Handles.ExpectAsync(VfsError.ReadOnly, () => RunAsync(fs, op));
            StringAssert.Contains(ex.Message, "mount is read-only", op);
            Assert.IsTrue(fs.ReadOnly);
            Assert.IsFalse(fs.CanWrite("files/existing.txt"));
            Assert.IsTrue(bundle.Source.Exists("existing.txt"), op);
            Assert.IsFalse(bundle.Source.Exists("new.txt"), op);
        }
    }

    [TestMethod]
    public async Task EveryWriteIsRefusedByTheMembershipLayer()
    {
        foreach (var op in Ops)
        {
            var (bundle, fs) = await SetupAsync(canWrite: true, membershipReadOnly: true, mountReadOnly: false);
            var ex = await Handles.ExpectAsync(VfsError.ReadOnly, () => RunAsync(fs, op));
            StringAssert.Contains(ex.Message, "read-only in collection 'work'", op);
            Assert.IsFalse(fs.ReadOnly, "the mount itself is writable");
            Assert.IsFalse(fs.CanWrite("files/existing.txt"));
            Assert.IsTrue(bundle.Source.Exists("existing.txt"), op);
        }
    }

    [TestMethod]
    public async Task EveryWriteIsRefusedByTheCapabilityLayer()
    {
        foreach (var op in Ops)
        {
            var (_, fs) = await SetupAsync(canWrite: false, membershipReadOnly: false, mountReadOnly: false);
            var ex = await Handles.ExpectAsync(VfsError.ReadOnly, () => RunAsync(fs, op));
            StringAssert.Contains(ex.Message, "can't write", op);
            Assert.IsFalse(fs.CanWrite("files/existing.txt"));
        }
    }

    [TestMethod]
    public async Task EveryWriteSucceedsWhenAllThreeAgree()
    {
        foreach (var op in Ops)
        {
            var (bundle, fs) = await SetupAsync(canWrite: true, membershipReadOnly: false, mountReadOnly: false);
            Assert.IsTrue(fs.CanWrite("files/existing.txt"));
            await RunAsync(fs, op);
            switch (op)
            {
                case "create":
                    Assert.IsTrue(bundle.Source.Exists("new.txt"));
                    break;
                case "write":
                    Assert.AreEqual("x", System.Text.Encoding.UTF8.GetString(bundle.Source.GetFile("existing.txt")!));
                    break;
                case "truncate":
                    Assert.AreEqual(0, bundle.Source.GetFile("existing.txt")!.Length);
                    break;
                case "mkdir":
                    Assert.IsTrue(bundle.Source.Exists("dir"));
                    break;
                case "delete":
                    Assert.IsFalse(bundle.Source.Exists("existing.txt"));
                    break;
                case "rename":
                    Assert.IsTrue(bundle.Source.Exists("moved.txt"));
                    break;
            }
        }
    }

    [TestMethod]
    public async Task TheSameSourceCanBeWritableInOneCollectionAndReadOnlyInAnother()
    {
        var source = new MemorySource().AddFile("existing.txt", "e");
        var handle = await Handles.ReadyAsync("src", source);
        var writable = Handles.Vfs("work", new CollectionMember(handle, "files", false));
        var locked = Handles.Vfs("archive", new CollectionMember(handle, "archive/files", true));

        await Handles.ExpectAsync(VfsError.ReadOnly, () => locked.CreateDirectoryAsync("archive/files/d", CancellationToken.None));
        await writable.CreateDirectoryAsync("files/d", CancellationToken.None);
        Assert.IsTrue(source.Exists("d"));
        CollectionAssert.AreEqual(new[] { "d/", "existing.txt" }, await Handles.NamesAsync(locked, "archive/files"), "the read-only view still sees the write");
    }

    [TestMethod]
    public async Task ReadsAreNeverAffectedByReadOnlyLayers()
    {
        var (_, fs) = await SetupAsync(canWrite: false, membershipReadOnly: true, mountReadOnly: true);
        Assert.AreEqual("e", await Handles.ReadTextAsync(fs, "files/existing.txt"));
        CollectionAssert.AreEqual(new[] { "existing.txt" }, await Handles.NamesAsync(fs, "files"));
        Assert.IsNotNull(await fs.StatAsync("files/existing.txt", CancellationToken.None));
    }

    private sealed record SourceHandleBundle(MemorySource Source);
}
