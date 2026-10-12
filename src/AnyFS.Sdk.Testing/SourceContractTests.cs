using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AnyFS.Sdk.Testing;

/// <summary>What a contract run needs: an initialised source and where to find known content in it.</summary>
/// <param name="Source">Initialised and ready.</param>
/// <param name="DirectoryPath">A directory that exists (the root, "", is fine) and contains <paramref name="FilePath"/>.</param>
/// <param name="FilePath">A file of at least 16 bytes whose contents are <paramref name="FileContents"/>.</param>
/// <param name="WritableDirectory">A directory the tests may create and delete things in; null when the source can't write.</param>
public sealed record SourceContractSetup(ISource Source, string DirectoryPath, string FilePath, byte[] FileContents, string? WritableDirectory = null);

/// <summary>
/// Checks the list/stat/read/range/write semantics every source must have. Derive a [TestClass] from it
/// and implement <see cref="SetUpAsync"/>; the source is disposed after each test.
/// </summary>
public abstract class SourceContractTests
{
    private SourceContractSetup? _setup;

    protected SourceContractSetup Setup => _setup ?? throw new InvalidOperationException("SetUpAsync has not run");

    protected ISource Source => Setup.Source;

    protected abstract Task<SourceContractSetup> SetUpAsync();

    [TestInitialize]
    public async Task ContractInitialise()
    {
        _setup = await SetUpAsync();
        Assert.IsTrue(_setup.FileContents.Length >= 16, "the contract file must be at least 16 bytes");
    }

    [TestCleanup]
    public async Task ContractCleanup()
    {
        if (_setup is not null)
        {
            await _setup.Source.DisposeAsync();
        }
    }

    [TestMethod]
    public async Task Contract_RootLists()
    {
        var entries = await ToListAsync(Source.ListAsync("", CancellationToken.None));
        Assert.IsNotNull(entries);
    }

    [TestMethod]
    public async Task Contract_StatRootIsDirectory()
    {
        var root = await Source.StatAsync("", CancellationToken.None);
        Assert.IsNotNull(root);
        Assert.IsTrue(root.IsDirectory);
    }

    [TestMethod]
    public async Task Contract_StatDirectory()
    {
        var dir = await Source.StatAsync(Setup.DirectoryPath, CancellationToken.None);
        Assert.IsNotNull(dir);
        Assert.IsTrue(dir.IsDirectory);
    }

    [TestMethod]
    public async Task Contract_StatFileHasLengthAndName()
    {
        var file = await Source.StatAsync(Setup.FilePath, CancellationToken.None);
        Assert.IsNotNull(file);
        Assert.IsFalse(file.IsDirectory);
        Assert.AreEqual(Setup.FileContents.Length, file.Length);
        Assert.AreEqual(VfsPath.Name(Setup.FilePath), file.Name);
    }

    [TestMethod]
    public async Task Contract_ListingContainsTheFile()
    {
        var entries = await ToListAsync(Source.ListAsync(Setup.DirectoryPath, CancellationToken.None));
        var name = VfsPath.Name(Setup.FilePath);
        var entry = entries.SingleOrDefault(e => e.Name == name);
        Assert.IsNotNull(entry, $"'{name}' missing from the listing of '{Setup.DirectoryPath}'");
        Assert.AreEqual(Setup.FileContents.Length, entry.Length);
        foreach (var e in entries)
        {
            Assert.IsFalse(e.Name.Contains('/'), "entry names must not contain separators");
            Assert.AreNotEqual("", e.Name);
        }
    }

    [TestMethod]
    public async Task Contract_StatMissingIsNull()
    {
        Assert.IsNull(await Source.StatAsync("definitely-not-here-9f1c", CancellationToken.None));
        Assert.IsNull(await Source.StatAsync(VfsPath.Join(Setup.DirectoryPath, "definitely-not-here-9f1c"), CancellationToken.None));
    }

    [TestMethod]
    public async Task Contract_ListMissingThrowsNotFound()
    {
        var ex = await Assert.ThrowsExactlyAsync<VfsException>(async () => await ToListAsync(Source.ListAsync("definitely-not-here-9f1c", CancellationToken.None)));
        Assert.AreEqual(VfsError.NotFound, ex.Error);
    }

    [TestMethod]
    public async Task Contract_ListFileThrowsNotADirectory()
    {
        var ex = await Assert.ThrowsExactlyAsync<VfsException>(async () => await ToListAsync(Source.ListAsync(Setup.FilePath, CancellationToken.None)));
        Assert.AreEqual(VfsError.NotADirectory, ex.Error);
    }

    [TestMethod]
    public async Task Contract_OpenMissingThrowsNotFound()
    {
        var ex = await Assert.ThrowsExactlyAsync<VfsException>(async () => await Source.OpenAsync("definitely-not-here-9f1c", VfsOpenMode.Read, CancellationToken.None));
        Assert.AreEqual(VfsError.NotFound, ex.Error);
    }

    [TestMethod]
    public async Task Contract_OpenDirectoryThrowsIsADirectory()
    {
        var ex = await Assert.ThrowsExactlyAsync<VfsException>(async () => await Source.OpenAsync(Setup.DirectoryPath, VfsOpenMode.Read, CancellationToken.None));
        Assert.AreEqual(VfsError.IsADirectory, ex.Error);
    }

    [TestMethod]
    public async Task Contract_ReadWholeFile()
    {
        await using var file = await Source.OpenAsync(Setup.FilePath, VfsOpenMode.Read, CancellationToken.None);
        Assert.AreEqual(Setup.FileContents.Length, file.Length);
        var buffer = new byte[file.Length];
        var n = await file.ReadFullyAsync(0, buffer, CancellationToken.None);
        Assert.AreEqual(buffer.Length, n);
        CollectionAssert.AreEqual(Setup.FileContents, buffer);
    }

    [TestMethod]
    public async Task Contract_RangedReads()
    {
        var expected = Setup.FileContents;
        await using var file = await Source.OpenAsync(Setup.FilePath, VfsOpenMode.Read, CancellationToken.None);

        var start = new byte[8];
        Assert.AreEqual(8, await file.ReadFullyAsync(0, start, CancellationToken.None));
        CollectionAssert.AreEqual(expected[..8], start);

        var midOffset = expected.Length / 2;
        var middle = new byte[8];
        Assert.AreEqual(8, await file.ReadFullyAsync(midOffset, middle, CancellationToken.None));
        CollectionAssert.AreEqual(expected[midOffset..(midOffset + 8)], middle);

        var tail = new byte[8];
        Assert.AreEqual(8, await file.ReadFullyAsync(expected.Length - 8, tail, CancellationToken.None));
        CollectionAssert.AreEqual(expected[^8..], tail);

        var over = new byte[16];
        Assert.AreEqual(4, await file.ReadFullyAsync(expected.Length - 4, over, CancellationToken.None), "a read crossing the end returns the bytes that exist");
        Assert.AreEqual(0, await file.ReadAsync(expected.Length, over, CancellationToken.None), "a read at the end returns 0");
        Assert.AreEqual(0, await file.ReadAsync(expected.Length + 100, over, CancellationToken.None), "a read past the end returns 0");
    }

    [TestMethod]
    public async Task Contract_ConcurrentReadsAgree()
    {
        await using var file = await Source.OpenAsync(Setup.FilePath, VfsOpenMode.Read, CancellationToken.None);
        var tasks = Enumerable.Range(0, 8).Select(async i =>
        {
            var offset = (Setup.FileContents.Length / 8) * i;
            var buffer = new byte[Math.Min(8, Setup.FileContents.Length - offset)];
            await file.ReadFullyAsync(offset, buffer, CancellationToken.None);
            CollectionAssert.AreEqual(Setup.FileContents[offset..(offset + buffer.Length)], buffer);
        });
        await Task.WhenAll(tasks);
    }

    [TestMethod]
    public async Task Contract_DotDotIsInvalid()
    {
        var ex = await Assert.ThrowsExactlyAsync<VfsException>(async () => await Source.StatAsync("../x", CancellationToken.None));
        Assert.AreEqual(VfsError.InvalidPath, ex.Error);
    }

    [TestMethod]
    public async Task Contract_WritesRefusedWhenNotCapable()
    {
        if (Source.Capabilities.CanWrite)
        {
            Assert.Inconclusive("the source can write; see the write round-trip tests");
        }

        var ex = await Assert.ThrowsExactlyAsync<VfsException>(async () => await Source.CreateDirectoryAsync("new-dir", CancellationToken.None));
        Assert.AreEqual(VfsError.NotSupported, ex.Error);
        ex = await Assert.ThrowsExactlyAsync<VfsException>(async () => await Source.OpenAsync("new-file", VfsOpenMode.Create, CancellationToken.None));
        Assert.AreEqual(VfsError.NotSupported, ex.Error);
        ex = await Assert.ThrowsExactlyAsync<VfsException>(async () => await Source.DeleteAsync(Setup.FilePath, CancellationToken.None));
        Assert.AreEqual(VfsError.NotSupported, ex.Error);
        ex = await Assert.ThrowsExactlyAsync<VfsException>(async () => await Source.RenameAsync(Setup.FilePath, Setup.FilePath + ".moved", false, CancellationToken.None));
        Assert.AreEqual(VfsError.NotSupported, ex.Error);
    }

    [TestMethod]
    public async Task Contract_WriteRoundTrip()
    {
        if (Setup.WritableDirectory is null)
        {
            Assert.Inconclusive("no writable directory was given");
        }

        Assert.IsTrue(Source.Capabilities.CanWrite, "a writable directory was given but Capabilities.CanWrite is false");
        var dir = VfsPath.Join(Setup.WritableDirectory, "contract-" + Guid.NewGuid().ToString("N")[..8]);
        await Source.CreateDirectoryAsync(dir, CancellationToken.None);
        var stat = await Source.StatAsync(dir, CancellationToken.None);
        Assert.IsNotNull(stat);
        Assert.IsTrue(stat.IsDirectory);

        var ex = await Assert.ThrowsExactlyAsync<VfsException>(async () => await Source.CreateDirectoryAsync(dir, CancellationToken.None));
        Assert.AreEqual(VfsError.AlreadyExists, ex.Error);

        var filePath = VfsPath.Join(dir, "written.txt");
        var payload = Encoding.UTF8.GetBytes("hello from the contract tests\n");
        await using (var file = await Source.OpenAsync(filePath, VfsOpenMode.CreateNew, CancellationToken.None))
        {
            await file.WriteAsync(0, payload, CancellationToken.None);
            await file.FlushAsync(CancellationToken.None);
        }

        ex = await Assert.ThrowsExactlyAsync<VfsException>(async () => await Source.OpenAsync(filePath, VfsOpenMode.CreateNew, CancellationToken.None));
        Assert.AreEqual(VfsError.AlreadyExists, ex.Error);

        var written = await Source.StatAsync(filePath, CancellationToken.None);
        Assert.IsNotNull(written);
        Assert.AreEqual(payload.Length, written.Length);

        await using (var file = await Source.OpenAsync(filePath, VfsOpenMode.Read, CancellationToken.None))
        {
            var back = new byte[payload.Length];
            Assert.AreEqual(payload.Length, await file.ReadFullyAsync(0, back, CancellationToken.None));
            CollectionAssert.AreEqual(payload, back);
        }

        await using (var file = await Source.OpenAsync(filePath, VfsOpenMode.ReadWrite, CancellationToken.None))
        {
            await file.SetLengthAsync(5, CancellationToken.None);
            await file.FlushAsync(CancellationToken.None);
        }

        Assert.AreEqual(5, (await Source.StatAsync(filePath, CancellationToken.None))!.Length);

        var moved = VfsPath.Join(dir, "moved.txt");
        await Source.RenameAsync(filePath, moved, false, CancellationToken.None);
        Assert.IsNull(await Source.StatAsync(filePath, CancellationToken.None));
        Assert.IsNotNull(await Source.StatAsync(moved, CancellationToken.None));

        var when = new DateTimeOffset(2024, 5, 6, 7, 8, 9, TimeSpan.Zero);
        await Source.SetTimesAsync(moved, null, when, CancellationToken.None);
        var touched = await Source.StatAsync(moved, CancellationToken.None);
        Assert.IsNotNull(touched?.Modified);
        Assert.IsTrue(Math.Abs((touched.Modified.Value - when).TotalSeconds) < 2, $"modified time {touched.Modified} should be {when}");

        ex = await Assert.ThrowsExactlyAsync<VfsException>(async () => await Source.DeleteAsync(dir, CancellationToken.None));
        Assert.AreEqual(VfsError.DirectoryNotEmpty, ex.Error);

        await Source.DeleteAsync(moved, CancellationToken.None);
        await Source.DeleteAsync(dir, CancellationToken.None);
        Assert.IsNull(await Source.StatAsync(dir, CancellationToken.None));
    }

    protected static async Task<List<VfsEntry>> ToListAsync(IAsyncEnumerable<VfsEntry> entries)
    {
        var list = new List<VfsEntry>();
        await foreach (var e in entries)
        {
            list.Add(e);
        }

        return list;
    }
}
