namespace AnyFS.Sdk.Tests;

[TestClass]
public class SequentialReadAheadTests
{
    // longer than two fills, with a tail that is not a whole fill
    private static readonly byte[] Data = Enumerable.Range(0, (2 << 20) + 12_345).Select(i => (byte)(i % 251)).ToArray();

    [TestMethod]
    public async Task SmallSequentialReadsCostOneSourceReadPerFill()
    {
        var file = new CountingFile(Data);
        using var ahead = new SequentialReadAhead();
        var all = new MemoryStream();
        var buffer = new byte[1000];
        long offset = 0;
        while (true)
        {
            var n = await ahead.ReadAsync(file, offset, buffer, CancellationToken.None);
            if (n == 0)
            {
                break;
            }

            all.Write(buffer, 0, n);
            offset += n;
        }

        CollectionAssert.AreEqual(Data, all.ToArray(), "sequential 1000-byte reads");
        // a fill starts where the read that overran the previous fill started; near the end a read that is not
        // wholly inside the (short) last fill fills again, and ReadFullyAsync probes once more after a short fill
        var fills = file.Reads.Where(r => r.Length == SequentialReadAhead.DefaultAheadBytes).Select(r => r.Offset).ToList();
        CollectionAssert.AreEqual(new long[] { 0, 1_048_000, 2_096_000, 2_109_000, Data.Length }, fills, "fills of 1 MiB");
        Assert.IsTrue(file.Reads.Count <= 8, $"the source saw {file.Reads.Count} reads, not one per 1000 bytes");
    }

    [TestMethod]
    public async Task ASeekAndABigReadGoToTheSourceAsTheyAre()
    {
        var file = new CountingFile(Data);
        using var ahead = new SequentialReadAhead();
        var small = new byte[4096];
        await ahead.ReadAsync(file, 0, small, CancellationToken.None);
        await ahead.ReadAsync(file, 4096, small, CancellationToken.None);
        Assert.AreEqual(1, file.Reads.Count, "the first read fills, the second is served from the fill");

        // a seek back inside what was read ahead costs nothing
        Assert.AreEqual(4096, await ahead.ReadAsync(file, 100, small, CancellationToken.None));
        CollectionAssert.AreEqual(Data[100..4196], small);
        Assert.AreEqual(1, file.Reads.Count);

        // a seek outside it: direct, the exact size asked for
        Assert.AreEqual(4096, await ahead.ReadAsync(file, 1_500_000, small, CancellationToken.None));
        CollectionAssert.AreEqual(Data[1_500_000..1_504_096], small);
        Assert.AreEqual((1_500_000L, 4096), file.Reads[^1]);

        // a read bigger than the threshold goes to the source whole, sequential or not
        var big = new byte[SequentialReadAhead.DefaultThreshold + 1];
        Assert.AreEqual(big.Length, await ahead.ReadAsync(file, 1_504_096, big, CancellationToken.None));
        CollectionAssert.AreEqual(Data[1_504_096..(1_504_096 + big.Length)], big);
        Assert.AreEqual((1_504_096L, big.Length), file.Reads[^1]);

        // a small read following on from it is sequential again: one fill (which runs into the end of the file,
        // so ReadFullyAsync probes once more after it)
        var next = 1_504_096L + big.Length;
        var before = file.Reads.Count;
        await ahead.ReadAsync(file, next, small, CancellationToken.None);
        Assert.AreEqual((next, SequentialReadAhead.DefaultAheadBytes), file.Reads[before]);
        CollectionAssert.AreEqual(Data[(int)next..(int)(next + 4096)], small);
    }

    [TestMethod]
    public async Task TheTailIsShortAndPastTheEndIsEmpty()
    {
        var file = new CountingFile(Data);
        using var ahead = new SequentialReadAhead();
        var buffer = new byte[1000];
        var tail = Data.Length - 500;
        Assert.AreEqual(500, await ahead.ReadAsync(file, tail, buffer, CancellationToken.None), "a seek to 500 bytes before the end");
        CollectionAssert.AreEqual(Data[tail..], buffer[..500]);
        Assert.AreEqual(0, await ahead.ReadAsync(file, Data.Length, buffer, CancellationToken.None));
        Assert.AreEqual(0, await ahead.ReadAsync(file, Data.Length + 10, buffer, CancellationToken.None));
    }

    [TestMethod]
    public async Task ForgetDropsWhatWasReadAheadSoAWriteIsSeen()
    {
        var file = new CountingFile(Data);
        using var ahead = new SequentialReadAhead();
        var buffer = new byte[100];
        await ahead.ReadAsync(file, 0, buffer, CancellationToken.None);

        var replacement = new byte[100];
        Array.Fill(replacement, (byte)0xAB);
        await file.WriteAsync(200, replacement, CancellationToken.None);
        ahead.Forget();

        Assert.AreEqual(100, await ahead.ReadAsync(file, 200, buffer, CancellationToken.None));
        CollectionAssert.AreEqual(replacement, buffer, "after Forget the next read comes from the file");
    }

    [TestMethod]
    public async Task TheFirstReadAtTheStartFillsWithoutAWarmUp()
    {
        var file = new CountingFile(Data);
        using var ahead = new SequentialReadAhead();
        var buffer = new byte[128 * 1024];
        await ahead.ReadAsync(file, 0, buffer, CancellationToken.None);
        Assert.AreEqual((0L, SequentialReadAhead.DefaultAheadBytes), file.Reads.Single());
        for (var offset = buffer.Length; offset < SequentialReadAhead.DefaultAheadBytes; offset += buffer.Length)
        {
            await ahead.ReadAsync(file, offset, buffer, CancellationToken.None);
        }

        Assert.AreEqual(1, file.Reads.Count, "eight 128 KiB reads from one fill");
    }

    /// <summary>A file over a byte array that records every read it is asked for.</summary>
    private sealed class CountingFile(byte[] data) : IVfsFile
    {
        private byte[] _data = data;

        public List<(long Offset, int Length)> Reads { get; } = [];

        public long Length => _data.Length;

        public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct)
        {
            Reads.Add((offset, buffer.Length));
            if (offset >= _data.Length)
            {
                return ValueTask.FromResult(0);
            }

            var n = (int)Math.Min(buffer.Length, _data.Length - offset);
            _data.AsMemory((int)offset, n).CopyTo(buffer);
            return ValueTask.FromResult(n);
        }

        public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            var end = offset + data.Length;
            if (end > _data.Length)
            {
                Array.Resize(ref _data, (int)end);
            }

            data.CopyTo(_data.AsMemory((int)offset, data.Length));
            return ValueTask.CompletedTask;
        }

        public ValueTask SetLengthAsync(long length, CancellationToken ct)
        {
            Array.Resize(ref _data, (int)length);
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushAsync(CancellationToken ct) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
