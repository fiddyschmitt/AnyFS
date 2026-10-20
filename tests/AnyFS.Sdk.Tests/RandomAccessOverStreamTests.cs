namespace AnyFS.Sdk.Tests;

[TestClass]
public class RandomAccessOverStreamTests
{
    private static readonly byte[] Data = Enumerable.Range(0, 100_000).Select(i => (byte)(i % 251)).ToArray();

    private static Task<Stream> OpenAt(long offset, CancellationToken ct)
        => Task.FromResult<Stream>(new MemoryStream(Data, (int)offset, Data.Length - (int)offset, writable: false));

    [TestMethod]
    public async Task SequentialReadsReuseOneStream()
    {
        await using var file = new RandomAccessOverStream(Data.Length, OpenAt);
        var buffer = new byte[4096];
        long offset = 0;
        while (offset < Data.Length)
        {
            var n = await file.ReadAsync(offset, buffer, CancellationToken.None);
            Assert.IsTrue(n > 0);
            CollectionAssert.AreEqual(Data[(int)offset..(int)(offset + n)], buffer[..n]);
            offset += n;
        }

        Assert.AreEqual(1, file.StreamsOpened);
        Assert.AreEqual(0, await file.ReadAsync(Data.Length, buffer, CancellationToken.None));
    }

    [TestMethod]
    public async Task AJumpOpensANewStreamAndASmallSkipDoesNot()
    {
        await using var file = new RandomAccessOverStream(Data.Length, OpenAt, maxStreams: 4, maxSkipBytes: 1024);
        var buffer = new byte[100];
        await file.ReadAsync(0, buffer, CancellationToken.None);
        Assert.AreEqual(1, file.StreamsOpened);

        await file.ReadAsync(600, buffer, CancellationToken.None);
        Assert.AreEqual(1, file.StreamsOpened, "600 is within the skip window of the stream at 100");
        CollectionAssert.AreEqual(Data[600..700], buffer);

        await file.ReadAsync(50_000, buffer, CancellationToken.None);
        Assert.AreEqual(2, file.StreamsOpened, "a far jump opens a second stream");
        CollectionAssert.AreEqual(Data[50_000..50_100], buffer);

        await file.ReadAsync(700, buffer, CancellationToken.None);
        Assert.AreEqual(2, file.StreamsOpened, "the first stream, still at 700, is reused");

        await file.ReadAsync(0, buffer, CancellationToken.None);
        Assert.AreEqual(3, file.StreamsOpened, "going backwards needs a new stream");
    }

    [TestMethod]
    public async Task EvictsTheLeastRecentlyUsedStreamWhenFull()
    {
        await using var file = new RandomAccessOverStream(Data.Length, OpenAt, maxStreams: 2, maxSkipBytes: 0);
        var buffer = new byte[10];
        await file.ReadAsync(0, buffer, CancellationToken.None);
        await file.ReadAsync(10_000, buffer, CancellationToken.None);
        await file.ReadAsync(20_000, buffer, CancellationToken.None);
        Assert.AreEqual(3, file.StreamsOpened);
        await file.ReadAsync(20_010, buffer, CancellationToken.None);
        await file.ReadAsync(10_010, buffer, CancellationToken.None);
        Assert.AreEqual(3, file.StreamsOpened, "both kept streams are at their expected positions");
        await file.ReadAsync(10, buffer, CancellationToken.None);
        Assert.AreEqual(4, file.StreamsOpened, "the stream at 10 was evicted earlier");
    }

    [TestMethod]
    public async Task ConcurrentReadsAreCorrect()
    {
        await using var file = new RandomAccessOverStream(Data.Length, OpenAt, maxStreams: 3);
        var tasks = Enumerable.Range(0, 16).Select(async i =>
        {
            var offset = i * 6000;
            var buffer = new byte[500];
            var n = await file.ReadFullyAsync(offset, buffer, CancellationToken.None);
            Assert.AreEqual(Math.Min(500, Data.Length - offset), n);
            CollectionAssert.AreEqual(Data[offset..(offset + n)], buffer[..n]);
        });
        await Task.WhenAll(tasks);
        Assert.IsTrue(file.StreamsOpened <= 16);
    }

    [TestMethod]
    public async Task AStreamThatGoesQuietFailsTheReadAndTheNextReadOpensAnother()
    {
        var opens = 0;
        Task<Stream> OpenQuietFirst(long offset, CancellationToken ct)
            => Interlocked.Increment(ref opens) == 1 ? Task.FromResult<Stream>(new QuietStream()) : OpenAt(offset, ct);

        await using var file = new RandomAccessOverStream(Data.Length, OpenQuietFirst, stallLimit: TimeSpan.FromMilliseconds(200));
        var buffer = new byte[100];
        var ex = await Assert.ThrowsExactlyAsync<VfsException>(async () => await file.ReadAsync(0, buffer, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.AreEqual(VfsError.Unavailable, ex.Error);
        StringAssert.Contains(ex.Message, "sent nothing");

        Assert.AreEqual(100, await file.ReadAsync(0, buffer, CancellationToken.None), "the quiet stream was dropped");
        CollectionAssert.AreEqual(Data[..100], buffer);
    }

    [TestMethod]
    public async Task ReadOnly()
    {
        await using var file = new RandomAccessOverStream(Data.Length, OpenAt);
        var ex = await Assert.ThrowsExactlyAsync<VfsException>(async () => await file.WriteAsync(0, new byte[1], CancellationToken.None));
        Assert.AreEqual(VfsError.NotSupported, ex.Error);
        Assert.AreEqual(0, await file.ReadAsync(Data.Length + 5, new byte[1], CancellationToken.None));
    }

    /// <summary>An upstream whose connection went quiet: a read waits until it is called off.</summary>
    internal sealed class QuietStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
