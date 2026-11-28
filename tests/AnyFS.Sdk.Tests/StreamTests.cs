using System.Text;
using AnyFS.Sdk.Testing;

namespace AnyFS.Sdk.Tests;

[TestClass]
public class StreamTests
{
    private static readonly byte[] Payload = Encoding.ASCII.GetBytes("0123456789abcdefghijklmnopqrstuvwxyz");

    /// <summary>A file that counts what is asked of it.</summary>
    private sealed class Counted(byte[] data) : IVfsFile
    {
        public int Reads { get; private set; }

        public int Closes { get; private set; }

        public long Length => data.Length;

        public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct)
        {
            Reads++;
            var n = (int)Math.Max(0, Math.Min(buffer.Length, data.Length - offset));
            data.AsMemory((int)Math.Min(offset, data.Length), n).CopyTo(buffer);
            return ValueTask.FromResult(n);
        }

        public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> written, CancellationToken ct) => throw VfsException.NotSupported("writing");

        public ValueTask SetLengthAsync(long length, CancellationToken ct) => throw VfsException.NotSupported("writing");

        public ValueTask FlushAsync(CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
        {
            Closes++;
            return ValueTask.CompletedTask;
        }
    }

    [TestMethod]
    public async Task VfsFileStream_OfAFileNotYetOpenedOpensItWhenItIsReadAndNotAtAllIfItNeverIs()
    {
        // an answer that has headers and no body (a HEAD, a "not modified") needs a file's length and nothing else
        var opens = 0;
        Counted? opened = null;
        Task<IVfsFile> Open(CancellationToken ct)
        {
            opens++;
            return Task.FromResult<IVfsFile>(opened = new Counted(Payload));
        }

        await using (var unread = new VfsFileStream(Payload.Length, Open))
        {
            Assert.AreEqual(Payload.Length, unread.Length);
            Assert.IsTrue(unread.CanSeek);
            unread.Position = 10;
            Assert.IsFalse(unread.IsOpened);
        }

        Assert.AreEqual(0, opens, "never read, never opened");

        var read = new VfsFileStream(Payload.Length, Open);
        read.Seek(10, SeekOrigin.Begin);
        var buffer = new byte[5];
        Assert.AreEqual(5, await read.ReadAsync(buffer));
        Assert.AreEqual("abcde", Encoding.ASCII.GetString(buffer));
        Assert.AreEqual(5, await read.ReadAsync(buffer));
        Assert.AreEqual("fghij", Encoding.ASCII.GetString(buffer));
        Assert.IsTrue(read.IsOpened);
        Assert.AreEqual(1, opens, "opened once, at its first read");
        await read.DisposeAsync();
        Assert.AreEqual(1, opened!.Closes, "and closed with the stream");
        Assert.ThrowsExactly<NotSupportedException>(() => read.SetLength(1), "read-only");
    }

    [TestMethod]
    public async Task VfsFileStream_ReadAheadAsksTheFileAMegabyteAtATimeForWhatIsCopiedOutInPieces()
    {
        var data = Enumerable.Range(0, 4 << 20).Select(i => (byte)(i * 13 % 251)).ToArray();
        var plain = new Counted(data);
        var ahead = new Counted(data);
        foreach (var (file, readAhead) in new[] { (plain, false), (ahead, true) })
        {
            await using var stream = new VfsFileStream(file, ownsFile: true, readAhead: readAhead);
            var copied = new MemoryStream();
            await stream.CopyToAsync(copied, 64 * 1024);
            CollectionAssert.AreEqual(data, copied.ToArray());
        }

        Assert.IsTrue(plain.Reads >= 64, $"{plain.Reads} reads without");
        Assert.IsTrue(ahead.Reads <= 6, $"{ahead.Reads} reads with it");
        Assert.AreEqual((1, 1), (plain.Closes, ahead.Closes));
    }

    [TestMethod]
    [DataRow("\"abc\"", null, true, DisplayName = "the tag")]
    [DataRow("W/\"abc\"", null, true, DisplayName = "the tag, weak")]
    [DataRow("\"x\", \"abc\"", null, true, DisplayName = "one of several")]
    [DataRow("*", null, true, DisplayName = "any")]
    [DataRow("\"x\"", null, false, DisplayName = "another tag")]
    [DataRow("\"x\"", "Thu, 01 Oct 2026 12:00:00 GMT", false, DisplayName = "another tag, whatever the time says")]
    [DataRow(null, "Thu, 01 Oct 2026 12:00:00 GMT", true, DisplayName = "not changed since, to the second")]
    [DataRow(null, "Thu, 01 Oct 2026 12:00:01 GMT", true, DisplayName = "not changed since a later time")]
    [DataRow(null, "Thu, 01 Oct 2026 11:59:59 GMT", false, DisplayName = "changed since")]
    [DataRow(null, "the first of October", false, DisplayName = "a time that is none")]
    [DataRow(null, null, false, DisplayName = "no validator")]
    public void HttpValidators_SayWhetherTheClientsCopyStillStands(string? ifNoneMatch, string? ifModifiedSince, bool expected)
    {
        var modified = new DateTimeOffset(2026, 10, 1, 12, 0, 0, 500, TimeSpan.Zero);
        Assert.AreEqual(expected, HttpValidators.NotModified(ifNoneMatch, ifModifiedSince, "\"abc\"", modified));
        Assert.IsTrue(HttpValidators.NotModified("\"{4A0C},3\"", null, "\"{4A0C},3\"", null), "a tag with a comma of its own");
        Assert.IsFalse(HttpValidators.NotModified(null, "Thu, 01 Oct 2026 12:00:00 GMT", "\"abc\"", null), "no time to go by");
    }

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
