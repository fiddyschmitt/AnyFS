using System.Text;
using static LibSmb.Tests.TestSupport;

namespace LibSmb.Tests;

/// <summary>The local folder backend, as portable-smb-server's localfs tests have it, ported.</summary>
[TestClass]
public class LocalFileSystemTests
{
    private string _dir = "";
    private LocalFileSystem _fs = null!;

    [TestInitialize]
    public void Setup()
    {
        _dir = TempDir();
        _fs = new LocalFileSystem(_dir);
    }

    [TestCleanup]
    public void Cleanup() => Remove(_dir);

    /// <summary>No path leaves the root.</summary>
    [TestMethod]
    public async Task PathsStayInTheRoot()
    {
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(_dir)!, "outside.txt"), "x");
        foreach (var path in new[] { "..", "../x", "a/../../x", "../outside.txt" })
        {
            try
            {
                var entry = await _fs.StatAsync(path, None);
                Assert.IsNull(entry, path);
            }
            catch (SmbFileSystemException ex)
            {
                Assert.AreEqual(SmbFileSystemError.AccessDenied, ex.Error, path);
            }
        }
    }

    /// <summary>Write, then rename and delete while a handle is open, which SMB clients do (FILE_SHARE_DELETE on Windows).</summary>
    [TestMethod]
    public async Task FilesRenameAndDeleteWhileOpen()
    {
        var file = await _fs.OpenAsync("hello.txt", SmbOpenMode.Write | SmbOpenMode.Create, None);
        await file.WriteAsync(0, "hello world"u8.ToArray(), None);
        await _fs.RenameAsync("hello.txt", "renamed.txt", false, None);
        await file.DisposeAsync();
        Assert.AreEqual(11, (await _fs.StatAsync("renamed.txt", None))!.Length);

        var reading = await _fs.OpenAsync("renamed.txt", SmbOpenMode.Read, None);
        await _fs.DeleteAsync("renamed.txt", None);
        await reading.DisposeAsync();
        Assert.IsNull(await _fs.StatAsync("renamed.txt", None));
    }

    [TestMethod]
    public async Task DirectoriesAreMadeListedAndDeletedWhenEmpty()
    {
        await _fs.CreateDirectoryAsync("sub", None);
        await (await _fs.OpenAsync("sub/a.txt", SmbOpenMode.Write | SmbOpenMode.Create, None)).DisposeAsync();
        var names = new List<string>();
        await foreach (var entry in _fs.ListAsync("sub", None))
        {
            names.Add(entry.Name);
        }

        CollectionAssert.AreEqual(new[] { "a.txt" }, names);
        var refused = await Assert.ThrowsExactlyAsync<SmbFileSystemException>(async () => await _fs.DeleteAsync("sub", None));
        Assert.AreEqual(SmbFileSystemError.DirectoryNotEmpty, refused.Error);
        await _fs.DeleteAsync("sub/a.txt", None);
        await _fs.DeleteAsync("sub", None);
        Assert.IsNull(await _fs.StatAsync("sub", None));

        var exists = await Assert.ThrowsExactlyAsync<SmbFileSystemException>(async () => await _fs.CreateDirectoryAsync("", None));
        Assert.AreEqual(SmbFileSystemError.AlreadyExists, exists.Error);
    }

    [TestMethod]
    public async Task SpaceIsReported()
    {
        var space = (await _fs.GetSpaceAsync(None))!.Value;
        Assert.IsGreaterThan(0, space.TotalBytes);
        Assert.IsGreaterThan(0, space.FreeBytes);
        Assert.IsLessThanOrEqualTo(space.TotalBytes, space.FreeBytes);
    }

    /// <summary>
    /// A file deleted while a handle is still open is gone from its folder at once, without that handle being closed: a
    /// client that keeps handles (macOS smbfs) deletes and expects the name gone, POSIX-style.
    /// </summary>
    [TestMethod]
    public async Task AFileDeletedWhileOpenIsGoneAtOnce()
    {
        var file = await _fs.OpenAsync("gone.txt", SmbOpenMode.Write | SmbOpenMode.Create, None);
        try
        {
            await file.WriteAsync(0, "data"u8.ToArray(), None);
            await _fs.DeleteAsync("gone.txt", None);
            Assert.IsNull(await _fs.StatAsync("gone.txt", None));
            await foreach (var entry in _fs.ListAsync("", None))
            {
                Assert.AreNotEqual("gone.txt", entry.Name);
            }
        }
        finally
        {
            await file.DisposeAsync();
        }
    }

    /// <summary>The open modes: exclusive create, truncate, open-or-create; reads at offsets; length; times set.</summary>
    [TestMethod]
    public async Task OpenModesReadsWritesAndTimes()
    {
        await (await _fs.OpenAsync("f.txt", SmbOpenMode.Write | SmbOpenMode.Create | SmbOpenMode.Exclusive, None)).DisposeAsync();
        var collision = await Assert.ThrowsExactlyAsync<SmbFileSystemException>(async () => await _fs.OpenAsync("f.txt", SmbOpenMode.Write | SmbOpenMode.Create | SmbOpenMode.Exclusive, None));
        Assert.AreEqual(SmbFileSystemError.AlreadyExists, collision.Error);

        await using (var file = await _fs.OpenAsync("f.txt", SmbOpenMode.Write, None))
        {
            await file.WriteAsync(4, "tail"u8.ToArray(), None);
            Assert.AreEqual(8, file.Length);
            var buffer = new byte[8];
            Assert.AreEqual(8, await file.ReadAsync(0, buffer, None));
            Assert.AreEqual("\0\0\0\0tail", Encoding.ASCII.GetString(buffer));
            Assert.AreEqual(0, await file.ReadAsync(8, buffer, None), "at the end");
            await file.SetLengthAsync(2, None);
            Assert.AreEqual(2, file.Length);
        }

        await using (var file = await _fs.OpenAsync("f.txt", SmbOpenMode.Write | SmbOpenMode.Truncate, None))
        {
            Assert.AreEqual(0, file.Length);
        }

        await Assert.ThrowsAsync<FileNotFoundException>(async () => await _fs.OpenAsync("missing.txt", SmbOpenMode.Read, None));

        var when = new DateTimeOffset(2020, 2, 3, 4, 5, 6, TimeSpan.Zero);
        await _fs.SetTimesAsync("f.txt", null, when, None);
        Assert.AreEqual(when, (await _fs.StatAsync("f.txt", None))!.Modified);
    }

    /// <summary>A lookup in another case on a case-insensitive file system finds the file, under the name as asked for.</summary>
    [TestMethod]
    public async Task ALookupInAnotherCase()
    {
        await File.WriteAllTextAsync(Path.Combine(_dir, "Users.txt"), "x");
        var entry = await _fs.StatAsync("users.txt", None);
        if (_fs.CaseInsensitive)
        {
            Assert.AreEqual("users.txt", entry!.Name);
        }
        else
        {
            Assert.IsNull(entry);
        }
    }
}
