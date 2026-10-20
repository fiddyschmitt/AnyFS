namespace AnyFS.Sdk.Tests;

[TestClass]
public class SpoolCacheTests
{
    private string _dir = "";
    private readonly FakeTime _time = new();

    [TestInitialize]
    public void Init()
    {
        _dir = Path.Combine(Path.GetTempPath(), "anyfs-tests", "spool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    [TestMethod]
    public async Task SubRangeBeforeTheDownloadGetsThereBlocksThenReturns()
    {
        var upstream = new DrippingUpstream(Bytes(1000));
        await using var cache = new SpoolCache(_dir, time: _time);
        await using var file = await cache.OpenAsync("k", 1000, upstream.OpenAsync, CancellationToken.None);
        Assert.AreEqual(1000, file.Length);

        var buffer = new byte[100];
        var read = file.ReadAsync(500, buffer, CancellationToken.None).AsTask();
        await Task.Delay(50);
        Assert.IsFalse(read.IsCompleted, "nothing has arrived yet");

        upstream.Release(300);
        await Task.Delay(50);
        Assert.IsFalse(read.IsCompleted, "offset 500 is still beyond the 300 bytes that arrived");

        upstream.Release(300);
        var n = await read.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(100, n);
        CollectionAssert.AreEqual(Bytes(1000)[500..600], buffer);

        // the beginning is served without waiting for the end
        Assert.AreEqual(100, await file.ReadAsync(0, buffer, CancellationToken.None));
        upstream.Release(1000);
        Assert.AreEqual(100, await file.ReadFullyAsync(900, buffer, CancellationToken.None));
        Assert.AreEqual(0, await file.ReadAsync(1000, buffer, CancellationToken.None));
    }

    [TestMethod]
    public async Task ConcurrentReadersShareOneDownload()
    {
        var upstream = new DrippingUpstream(Bytes(5000));
        upstream.Release(5000);
        await using var cache = new SpoolCache(_dir, time: _time);
        var files = new List<IVfsFile>();
        for (var i = 0; i < 4; i++)
        {
            files.Add(await cache.OpenAsync("same", 5000, upstream.OpenAsync, CancellationToken.None));
        }

        var tasks = files.Select(async (f, i) =>
        {
            var buffer = new byte[1000];
            Assert.AreEqual(1000, await f.ReadFullyAsync(i * 1000, buffer, CancellationToken.None));
            CollectionAssert.AreEqual(Bytes(5000)[(i * 1000)..((i + 1) * 1000)], buffer);
        });
        await Task.WhenAll(tasks);
        Assert.AreEqual(1, upstream.Opens);
        Assert.AreEqual(1, cache.Count);
        foreach (var f in files)
        {
            await f.DisposeAsync();
        }

        Assert.AreEqual(1, cache.Count, "a finished spool stays for the next reader");
        await using var again = await cache.OpenAsync("same", 5000, upstream.OpenAsync, CancellationToken.None);
        Assert.AreEqual(1, upstream.Opens, "the finished spool was reused");
    }

    [TestMethod]
    public async Task ClosingTheLastReaderOfAnUnfinishedSpoolStopsTheDownload()
    {
        var upstream = new DrippingUpstream(Bytes(1000));
        await using var cache = new SpoolCache(_dir, time: _time);
        var file = await cache.OpenAsync("k", 1000, upstream.OpenAsync, CancellationToken.None);
        upstream.Release(100);
        await Task.Delay(50);
        await file.DisposeAsync();
        await Task.Delay(100);
        Assert.AreEqual(0, cache.Count);
        Assert.AreEqual(0, Directory.GetFiles(_dir, "*.spool").Length, "the partial file is deleted");
        Assert.IsTrue(upstream.Cancelled, "the pump was cancelled");

        await using var fresh = await cache.OpenAsync("k", 1000, upstream.OpenAsync, CancellationToken.None);
        Assert.AreEqual(2, upstream.Opens, "a new reader starts a new download");
    }

    [TestMethod]
    public async Task EvictionBySizeAndAge()
    {
        var data = Bytes(1000);
        await using var cache = new SpoolCache(_dir, maxBytes: 2500, maxAge: TimeSpan.FromHours(1), time: _time);
        for (var i = 0; i < 3; i++)
        {
            var up = new DrippingUpstream(data);
            up.Release(1000);
            await using var f = await cache.OpenAsync("k" + i, 1000, up.OpenAsync, CancellationToken.None);
            await f.ReadFullyAsync(0, new byte[1000], CancellationToken.None);
            await WaitForCompleteAsync(f);
            _time.Advance(TimeSpan.FromMinutes(1));
        }

        Assert.AreEqual(3, cache.Count);
        var up4 = new DrippingUpstream(data);
        up4.Release(1000);
        await using (await cache.OpenAsync("k3", 1000, up4.OpenAsync, CancellationToken.None))
        {
            Assert.AreEqual(3, cache.Count, "the oldest finished spool made room for the new one");
        }

        _time.Advance(TimeSpan.FromHours(2));
        var up5 = new DrippingUpstream(data);
        up5.Release(1000);
        await using (await cache.OpenAsync("k4", 1000, up5.OpenAsync, CancellationToken.None))
        {
            Assert.AreEqual(1, cache.Count, "everything older than maxAge went");
        }
    }

    [TestMethod]
    public async Task UpstreamFailureSurfacesToReadersPastTheAvailableBytes()
    {
        var upstream = new DrippingUpstream(Bytes(1000)) { FailAfter = 200 };
        await using var cache = new SpoolCache(_dir, time: _time);
        await using var file = await cache.OpenAsync("k", 1000, upstream.OpenAsync, CancellationToken.None);
        upstream.Release(1000);
        var buffer = new byte[100];
        Assert.AreEqual(100, await file.ReadFullyAsync(0, buffer, CancellationToken.None), "bytes before the failure are fine");
        var ex = await Assert.ThrowsExactlyAsync<VfsException>(async () => await file.ReadAsync(500, buffer, CancellationToken.None));
        Assert.AreEqual(VfsError.IoError, ex.Error);
    }

    [TestMethod]
    public async Task AnUpstreamThatGoesQuietFailsItsReaders()
    {
        await using var cache = new SpoolCache(_dir, time: _time, stallLimit: TimeSpan.FromMilliseconds(200));
        await using var file = await cache.OpenAsync("k", 1000, _ => Task.FromResult<Stream>(new RandomAccessOverStreamTests.QuietStream()), CancellationToken.None);
        var ex = await Assert.ThrowsExactlyAsync<VfsException>(async () => await file.ReadAsync(0, new byte[100], CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.AreEqual(VfsError.IoError, ex.Error);
        StringAssert.Contains(ex.Message, "sent nothing");

        await using var again = await cache.OpenAsync("k", 4, _ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3, 4])), CancellationToken.None);
        Assert.AreEqual(4, await again.ReadFullyAsync(0, new byte[4], CancellationToken.None), "the failed download is not handed to the next open");
    }

    [TestMethod]
    public async Task LeftoverSpoolFilesAreRemovedAtStart()
    {
        File.WriteAllText(Path.Combine(_dir, "stale.spool"), "x");
        await using var cache = new SpoolCache(_dir, time: _time);
        Assert.AreEqual(0, Directory.GetFiles(_dir, "*.spool").Length);
    }

    private static async Task WaitForCompleteAsync(IVfsFile file)
    {
        var spooling = (SpoolingFile)file;
        for (var i = 0; i < 100 && !spooling.IsComplete; i++)
        {
            await Task.Delay(10);
        }

        Assert.IsTrue(spooling.IsComplete);
    }

    private static byte[] Bytes(int n)
    {
        var b = new byte[n];
        for (var i = 0; i < n; i++)
        {
            b[i] = (byte)(i * 7 + 3);
        }

        return b;
    }

    /// <summary>A sequential upstream that only hands out bytes the test has released.</summary>
    private sealed class DrippingUpstream(byte[] data)
    {
        private readonly SemaphoreSlim _released = new(0, int.MaxValue);
        private readonly byte[] _data = data;
        private int _opens;

        public int Opens => _opens;

        public bool Cancelled { get; private set; }

        public int FailAfter { get; set; } = int.MaxValue;

        public void Release(int bytes) => _released.Release(bytes);

        public Task<Stream> OpenAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _opens);
            return Task.FromResult<Stream>(new DripStream(this));
        }

        private sealed class DripStream(DrippingUpstream owner) : Stream
        {
            private int _position;

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position { get => _position; set => throw new NotSupportedException(); }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (_position >= owner._data.Length)
                {
                    return 0;
                }

                if (_position >= owner.FailAfter)
                {
                    throw new IOException("upstream broke");
                }

                try
                {
                    await owner._released.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    owner.Cancelled = true;
                    throw;
                }

                var n = 1;
                while (n < buffer.Length && _position + n < owner._data.Length && owner._released.Wait(0))
                {
                    n++;
                }

                n = (int)Math.Min(n, owner.FailAfter - (long)_position);

                owner._data.AsMemory(_position, n).CopyTo(buffer);
                _position += n;
                return n;
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
}
