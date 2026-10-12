using System.Text;
using AnyFS.Sdk.Testing;

namespace AnyFS.Sdk.Tests;

[TestClass]
public class StreamTests
{
    private static readonly byte[] Payload = Encoding.ASCII.GetBytes("0123456789abcdefghijklmnopqrstuvwxyz");

    [TestMethod]
    public async Task BytesVfsFile_ReadsRangesAndStopsAtEnd()
    {
        await using var file = new BytesVfsFile(Payload);
        Assert.AreEqual(Payload.Length, file.Length);
        var buffer = new byte[10];
        Assert.AreEqual(10, await file.ReadAsync(0, buffer, CancellationToken.None));
        Assert.AreEqual("0123456789", Encoding.ASCII.GetString(buffer));
        Assert.AreEqual(6, await file.ReadAsync(30, buffer, CancellationToken.None));
        Assert.AreEqual("uvwxyz", Encoding.ASCII.GetString(buffer, 0, 6));
        Assert.AreEqual(0, await file.ReadAsync(36, buffer, CancellationToken.None));
        Assert.AreEqual(0, await file.ReadAsync(100, buffer, CancellationToken.None));
        var ex = await Assert.ThrowsExactlyAsync<VfsException>(async () => await file.WriteAsync(0, buffer, CancellationToken.None));
        Assert.AreEqual(VfsError.NotSupported, ex.Error);
    }

    [TestMethod]
    public async Task VfsFileStream_SeeksAndReadsLikeAStream()
    {
        await using var stream = new VfsFileStream(new BytesVfsFile(Payload));
        Assert.IsTrue(stream.CanRead);
        Assert.IsTrue(stream.CanSeek);
        Assert.IsFalse(stream.CanWrite);
        Assert.AreEqual(Payload.Length, stream.Length);

        var buffer = new byte[5];
        Assert.AreEqual(5, await stream.ReadAsync(buffer));
        Assert.AreEqual("01234", Encoding.ASCII.GetString(buffer));
        Assert.AreEqual(5, stream.Position);

        stream.Seek(-3, SeekOrigin.End);
        Assert.AreEqual(3, stream.Read(buffer, 0, 5));
        Assert.AreEqual("xyz", Encoding.ASCII.GetString(buffer, 0, 3));
        Assert.AreEqual(0, stream.Read(buffer, 0, 5));

        stream.Position = 10;
        Assert.AreEqual(2, stream.Seek(-8, SeekOrigin.Current));
        Assert.AreEqual(2, stream.Position);
        Assert.ThrowsExactly<IOException>(() => stream.Seek(-1, SeekOrigin.Begin));
        Assert.ThrowsExactly<NotSupportedException>(() => stream.Write(buffer, 0, 1));
    }

    [TestMethod]
    public async Task VfsFileStream_WritesThroughToTheFile()
    {
        var source = new MemorySource().AddFile("a.txt", "hello");
        var file = await source.OpenAsync("a.txt", VfsOpenMode.ReadWrite, CancellationToken.None);
        await using (var stream = new VfsFileStream(file, writable: true, ownsFile: true))
        {
            stream.Seek(0, SeekOrigin.End);
            await stream.WriteAsync(Encoding.ASCII.GetBytes(" world"));
            await stream.FlushAsync();
            Assert.AreEqual(11, stream.Length);
            stream.SetLength(5);
            Assert.AreEqual(5, stream.Length);
            Assert.AreEqual(5, stream.Position);
            stream.Write(Encoding.ASCII.GetBytes("!"));
        }

        Assert.AreEqual("hello!", Encoding.UTF8.GetString(source.GetFile("a.txt")!));
    }

    [TestMethod]
    public async Task ReadFully_LoopsOverShortReads()
    {
        await using var file = new ChunkyFile(Payload, 4);
        var buffer = new byte[Payload.Length];
        Assert.AreEqual(Payload.Length, await file.ReadFullyAsync(0, buffer, CancellationToken.None));
        CollectionAssert.AreEqual(Payload, buffer);
        Assert.AreEqual(6, await file.ReadFullyAsync(30, buffer, CancellationToken.None));
    }

    [TestMethod]
    public async Task SourceBase_DefaultsAreSensible()
    {
        var source = new MinimalSource();
        using var context = new TestSourceContext("minimal");
        await source.InitialiseAsync(context, CancellationToken.None);

        var root = await source.StatAsync("", CancellationToken.None);
        Assert.IsTrue(root!.IsDirectory);
        var file = await source.StatAsync("hello.txt", CancellationToken.None);
        Assert.IsNotNull(file);
        Assert.AreEqual(5, file.Length);
        Assert.IsNull(await source.StatAsync("missing", CancellationToken.None));
        Assert.IsNull(await source.StatAsync("missing/deeper", CancellationToken.None));
        Assert.IsNull(await source.StatAsync("HELLO.TXT", CancellationToken.None), "a case-sensitive source does not match by case");

        var ex = await Assert.ThrowsExactlyAsync<VfsException>(async () => await source.CreateDirectoryAsync("x", CancellationToken.None));
        Assert.AreEqual(VfsError.NotSupported, ex.Error);
        ex = await Assert.ThrowsExactlyAsync<VfsException>(async () => await source.DeleteAsync("hello.txt", CancellationToken.None));
        Assert.AreEqual(VfsError.NotSupported, ex.Error);
        ex = await Assert.ThrowsExactlyAsync<VfsException>(async () => await source.RenameAsync("hello.txt", "b", false, CancellationToken.None));
        Assert.AreEqual(VfsError.NotSupported, ex.Error);
        ex = await Assert.ThrowsExactlyAsync<VfsException>(async () => await source.SetTimesAsync("hello.txt", null, DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.AreEqual(VfsError.NotSupported, ex.Error);
        Assert.IsNull(await source.GetSpaceAsync("", CancellationToken.None));
    }

    [TestMethod]
    public void VfsException_MapsIoExceptions()
    {
        Assert.AreEqual(VfsError.NotFound, VfsException.FromIo(new FileNotFoundException(), "p").Error);
        Assert.AreEqual(VfsError.NotFound, VfsException.FromIo(new DirectoryNotFoundException(), "p").Error);
        Assert.AreEqual(VfsError.AccessDenied, VfsException.FromIo(new UnauthorizedAccessException(), "p").Error);
        Assert.AreEqual(VfsError.IoError, VfsException.FromIo(new IOException("disk"), "p").Error);
        Assert.AreEqual(VfsError.NotSupported, VfsException.FromIo(new NotSupportedException(), "p").Error);
        var existing = VfsException.AlreadyExists("x");
        Assert.AreSame(existing, VfsException.FromIo(existing, "p"));
    }

    /// <summary>Returns at most <c>chunk</c> bytes per read, like a network source would.</summary>
    private sealed class ChunkyFile(byte[] data, int chunk) : IVfsFile
    {
        public long Length => data.Length;

        public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct)
        {
            if (offset >= data.Length)
            {
                return ValueTask.FromResult(0);
            }

            var n = (int)Math.Min(Math.Min(chunk, buffer.Length), data.Length - offset);
            data.AsMemory((int)offset, n).CopyTo(buffer);
            return ValueTask.FromResult(n);
        }

        public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> d, CancellationToken ct) => throw new NotSupportedException();

        public ValueTask SetLengthAsync(long length, CancellationToken ct) => throw new NotSupportedException();

        public ValueTask FlushAsync(CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>The smallest read-only plugin: List and Open only.</summary>
    private sealed class MinimalSource : SourceBase
    {
        public override async IAsyncEnumerable<VfsEntry> ListAsync(string path, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            if (VfsPath.Normalize(path).Length != 0)
            {
                throw VfsException.NotFound(path);
            }

            yield return VfsEntry.File("hello.txt", 5);
            await Task.CompletedTask;
        }

        public override Task<IVfsFile> OpenAsync(string path, VfsOpenMode mode, CancellationToken ct)
            => Task.FromResult<IVfsFile>(new BytesVfsFile(Encoding.ASCII.GetBytes("hello")));
    }
}
