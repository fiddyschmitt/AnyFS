using System.Net;
using LibSmb.Protocol;

namespace LibSmb.Tests;

/// <summary>
/// One connection carries everything a client machine asks of a server: every program's requests, over every open. A
/// request whose backend call is slow (a cold fetch from a remote store) must hold up only itself, while one open's
/// requests still take their turns.
/// </summary>
[TestClass]
public sealed class ConcurrencyTests
{
    private const string User = "user";
    private const string Password = "password";

    /// <summary>How long a request that nothing holds may take to be answered here.</summary>
    private static readonly TimeSpan Prompt = TimeSpan.FromSeconds(5);

    private static readonly byte[] SlowContent = [.. Enumerable.Range(0, 64 << 10).Select(i => (byte)(i * 7))];
    private static readonly byte[] FastContent = [.. Enumerable.Range(0, 16 << 10).Select(i => (byte)(i * 13))];

    private string _dir = null!;

    [TestInitialize]
    public void Setup()
    {
        _dir = TestSupport.TempDir();
        File.WriteAllBytes(Path.Combine(_dir, "slow.bin"), SlowContent);
        File.WriteAllBytes(Path.Combine(_dir, "fast.bin"), FastContent);
        Directory.CreateDirectory(Path.Combine(_dir, "docs"));
    }

    [TestCleanup]
    public void Cleanup() => TestSupport.Remove(_dir);

    /// <summary>
    /// A READ waits on its backend; a listing and a READ of another file sent after it on the same connection are answered
    /// meanwhile, signed (SMB 3.0.2, AES-CMAC), and the held READ is answered once it is let go.
    /// </summary>
    [TestMethod]
    public async Task AHeldReadHoldsUpNothingElseOnItsConnection()
    {
        var fs = new HeldFileSystem(_dir);
        await using var server = Start(fs);
        await using var client = await SmbTestClient.SignInAsync(server.LocalEndPoint, User, Password);
        Assert.AreEqual(Dialect.Smb302, client.Dialect);
        var tree = await client.TreeConnectAsync("data");
        var slow = await client.OpenAsync(tree, "slow.bin");
        var fast = await client.OpenAsync(tree, "fast.bin");
        var root = await client.OpenAsync(tree, "", options: CreateOptions.DirectoryFile);

        fs.Hold("slow.bin");
        ulong slowRead;
        try
        {
            slowRead = await client.PostAsync(Command.Read, SmbTestClient.ReadBody(slow, 0, SlowContent.Length), tree);
            await fs.Reached("slow.bin").WaitAsync(Prompt);
            var listing = await client.PostAsync(Command.QueryDirectory, SmbTestClient.QueryDirectoryBody(root, flags: QueryDirectoryFlags.RestartScans), tree);
            var fastRead = await client.PostAsync(Command.Read, SmbTestClient.ReadBody(fast, 0, FastContent.Length), tree);

            var answered = new Dictionary<ulong, SmbResponse>();
            try
            {
                for (var i = 0; i < 2; i++)
                {
                    var response = await client.ReceiveFinalAsync(Prompt);
                    answered[response.MessageId] = response;
                }
            }
            catch (TimeoutException)
            {
                Assert.Fail($"with a READ held, {answered.Count} of the 2 requests sent after it were answered within {Prompt.TotalSeconds} s");
            }

            Assert.IsTrue(answered.ContainsKey(listing) && answered.ContainsKey(fastRead), "the listing and the other READ were answered, not the held one");
            CollectionAssert.IsSubsetOf(new[] { "slow.bin", "fast.bin", "docs" }, SmbTestClient.DirectoryNames(answered[listing].EnsureSuccess(), 0x25));
            CollectionAssert.AreEqual(FastContent, SmbTestClient.ReadData(answered[fastRead]));
        }
        finally
        {
            fs.Release("slow.bin");
        }

        var held = await client.ReceiveFinalAsync(Prompt);
        Assert.AreEqual(slowRead, held.MessageId);
        CollectionAssert.AreEqual(SlowContent, SmbTestClient.ReadData(held));
    }

    /// <summary>Two READs of one open in flight together: the second reaches the backend only after the first is done.</summary>
    [TestMethod]
    public async Task OneOpensRequestsStillTakeTurns()
    {
        var fs = new HeldFileSystem(_dir);
        await using var server = Start(fs);
        await using var client = await SmbTestClient.SignInAsync(server.LocalEndPoint, User, Password);
        var tree = await client.TreeConnectAsync("data");
        var slow = await client.OpenAsync(tree, "slow.bin");
        const int half = 32 << 10;

        fs.Hold("slow.bin");
        try
        {
            await client.PostAsync(Command.Read, SmbTestClient.ReadBody(slow, 0, half), tree);
            await client.PostAsync(Command.Read, SmbTestClient.ReadBody(slow, half, half), tree);
            await fs.Reached("slow.bin").WaitAsync(Prompt);
            await Task.Delay(300);
        }
        finally
        {
            fs.Release("slow.bin");
        }

        var data = new List<byte[]> { SmbTestClient.ReadData(await client.ReceiveFinalAsync(Prompt)), SmbTestClient.ReadData(await client.ReceiveFinalAsync(Prompt)) };
        Assert.AreEqual(1, fs.MostAtOnce, "calls on one open overlapped");
        Assert.IsTrue(data.Any(d => d.SequenceEqual(SlowContent[..half])) && data.Any(d => d.SequenceEqual(SlowContent[half..])), "both halves read");
    }

    /// <summary>A client that goes away while its READ is held: the backend call is cancelled and the open closed.</summary>
    [TestMethod]
    public async Task AClientThatGoesAwayEndsItsHeldRequests()
    {
        var fs = new HeldFileSystem(_dir);
        await using var server = Start(fs);
        var client = await SmbTestClient.SignInAsync(server.LocalEndPoint, User, Password);
        var tree = await client.TreeConnectAsync("data");
        var slow = await client.OpenAsync(tree, "slow.bin");
        fs.Hold("slow.bin");
        try
        {
            await client.PostAsync(Command.Read, SmbTestClient.ReadBody(slow, 0, SlowContent.Length), tree);
            await fs.Reached("slow.bin").WaitAsync(Prompt);
            await client.DisposeAsync();

            var deadline = DateTime.UtcNow + Prompt;
            while ((fs.Cancelled == 0 || fs.Closed == 0 || !server.Connections.IsEmpty) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            Assert.AreEqual(1, fs.Cancelled, "the held READ's backend call was cancelled");
            Assert.AreEqual(1, fs.Closed, "the open was closed");
            Assert.IsTrue(server.Connections.IsEmpty, "the connection was let go");
        }
        finally
        {
            fs.Release("slow.bin");
        }
    }

    /// <summary>
    /// With two requests at a time allowed and two READs held, a third request is not taken up until one of them is done;
    /// then it is.
    /// </summary>
    [TestMethod]
    public async Task AConnectionWorksOnAsManyRequestsAtOnceAsItsSettingAllows()
    {
        var fs = new HeldFileSystem(_dir);
        await using var server = Start(fs, maxConcurrentRequests: 2);
        await using var client = await SmbTestClient.SignInAsync(server.LocalEndPoint, User, Password);
        var tree = await client.TreeConnectAsync("data");
        var slow = await client.OpenAsync(tree, "slow.bin");
        var fast = await client.OpenAsync(tree, "fast.bin");
        var root = await client.OpenAsync(tree, "", options: CreateOptions.DirectoryFile);

        fs.Hold("slow.bin");
        fs.Hold("fast.bin");
        try
        {
            var slowRead = await client.PostAsync(Command.Read, SmbTestClient.ReadBody(slow, 0, SlowContent.Length), tree);
            var fastRead = await client.PostAsync(Command.Read, SmbTestClient.ReadBody(fast, 0, FastContent.Length), tree);
            await Task.WhenAll(fs.Reached("slow.bin"), fs.Reached("fast.bin")).WaitAsync(Prompt);
            var listing = await client.PostAsync(Command.QueryDirectory, SmbTestClient.QueryDirectoryBody(root, flags: QueryDirectoryFlags.RestartScans), tree);
            await Task.Delay(500);
            Assert.AreEqual(0, fs.Listed, "a third request was taken up with two held");

            fs.Release("fast.bin");
            var answered = new[] { await client.ReceiveFinalAsync(Prompt), await client.ReceiveFinalAsync(Prompt) }.ToDictionary(r => r.MessageId);
            Assert.IsTrue(answered.ContainsKey(fastRead) && answered.ContainsKey(listing), "the freed slot took the listing");
            Assert.AreEqual(1, fs.Listed);

            fs.Release("slow.bin");
            Assert.AreEqual(slowRead, (await client.ReceiveFinalAsync(Prompt)).MessageId);
        }
        finally
        {
            fs.Release("slow.bin");
            fs.Release("fast.bin");
        }
    }

    /// <summary>
    /// A READ still running after a moment is answered STATUS_PENDING at once: asynchronous, with an AsyncId and the
    /// credits it asked for, unsigned ([MS-SMB2] 3.3.4.2); its real reply comes when it is done, under the same AsyncId,
    /// signed, granting no more credits.
    /// </summary>
    [TestMethod]
    public async Task ARequestStillRunningIsAnsweredPendingAndThenForReal()
    {
        var fs = new HeldFileSystem(_dir);
        await using var server = Start(fs);
        await using var client = await SmbTestClient.SignInAsync(server.LocalEndPoint, User, Password);
        var tree = await client.TreeConnectAsync("data");
        var slow = await client.OpenAsync(tree, "slow.bin");
        fs.Hold("slow.bin");
        SmbResponse interim;
        ulong read;
        try
        {
            read = await client.PostAsync(Command.Read, SmbTestClient.ReadBody(slow, 0, SlowContent.Length), tree);
            interim = await client.ReceiveAsync(Prompt);
        }
        finally
        {
            fs.Release("slow.bin");
        }

        Assert.AreEqual(Status.Pending, interim.Status, $"0x{interim.Status:X8}");
        Assert.AreEqual(read, interim.MessageId);
        Assert.IsTrue(interim.Async && interim.AsyncId != 0, "an asynchronous reply with an AsyncId");
        Assert.IsGreaterThanOrEqualTo(1, (int)interim.Credits, "the interim reply grants the credits");
        Assert.IsFalse(interim.Signed, "an interim reply is not signed");

        var final = await client.ReceiveAsync(Prompt);
        Assert.AreEqual(read, final.MessageId);
        Assert.IsTrue(final.Async, "the real reply is asynchronous too");
        Assert.AreEqual(interim.AsyncId, final.AsyncId);
        Assert.AreEqual(0, (int)final.Credits, "the credits came with the interim reply");
        Assert.IsTrue(final.Signed, "the real reply is signed (the client checked it)");
        CollectionAssert.AreEqual(SlowContent, SmbTestClient.ReadData(final));
    }

    /// <summary>A CANCEL of a held READ by its AsyncId: the backend call is cancelled, the READ answered STATUS_CANCELLED, and the connection carries on.</summary>
    [TestMethod]
    public async Task ACancelByAsyncIdEndsAHeldRequest()
    {
        var fs = new HeldFileSystem(_dir);
        await using var server = Start(fs);
        await using var client = await SmbTestClient.SignInAsync(server.LocalEndPoint, User, Password);
        var tree = await client.TreeConnectAsync("data");
        var slow = await client.OpenAsync(tree, "slow.bin");
        fs.Hold("slow.bin");
        try
        {
            var read = await client.PostAsync(Command.Read, SmbTestClient.ReadBody(slow, 0, SlowContent.Length), tree);
            var interim = await client.ReceiveAsync(Prompt);
            Assert.AreEqual(Status.Pending, interim.Status, $"0x{interim.Status:X8}");
            await client.CancelAsync(read, interim.AsyncId);
            var final = await client.ReceiveAsync(Prompt);
            Assert.AreEqual(read, final.MessageId);
            Assert.AreEqual(Status.Cancelled, final.Status, $"0x{final.Status:X8}");
            Assert.AreEqual(interim.AsyncId, final.AsyncId);
            Assert.AreEqual(1, fs.Cancelled, "the backend call was cancelled");
        }
        finally
        {
            fs.Release("slow.bin");
        }

        var fast = await client.OpenAsync(tree, "fast.bin");
        CollectionAssert.AreEqual(FastContent, await client.ReadAsync(tree, fast, 0, FastContent.Length), "the connection carries on");
    }

    /// <summary>A CANCEL of a held READ by its MessageId (sent before or after an interim reply): STATUS_CANCELLED.</summary>
    [TestMethod]
    public async Task ACancelByMessageIdEndsAHeldRequest()
    {
        var fs = new HeldFileSystem(_dir);
        await using var server = Start(fs);
        await using var client = await SmbTestClient.SignInAsync(server.LocalEndPoint, User, Password);
        var tree = await client.TreeConnectAsync("data");
        var slow = await client.OpenAsync(tree, "slow.bin");
        fs.Hold("slow.bin");
        try
        {
            var read = await client.PostAsync(Command.Read, SmbTestClient.ReadBody(slow, 0, SlowContent.Length), tree);
            await fs.Reached("slow.bin").WaitAsync(Prompt);
            await client.CancelAsync(read);
            var final = await client.ReceiveAsync(Prompt);
            if (final.Status == Status.Pending)
            {
                final = await client.ReceiveAsync(Prompt);
            }

            Assert.AreEqual(read, final.MessageId);
            Assert.AreEqual(Status.Cancelled, final.Status, $"0x{final.Status:X8}");
            Assert.AreEqual(1, fs.Cancelled, "the backend call was cancelled");
        }
        finally
        {
            fs.Release("slow.bin");
        }
    }

    /// <summary>
    /// A backend call that gives up on its own (an HTTP time-out, say) fails its request with STATUS_IO_TIMEOUT; the
    /// connection, which carries every other program's requests and opens, carries on.
    /// </summary>
    [TestMethod]
    public async Task ABackendThatGivesUpFailsItsRequestNotTheConnection()
    {
        var fs = new HeldFileSystem(_dir);
        await using var server = Start(fs);
        await using var client = await SmbTestClient.SignInAsync(server.LocalEndPoint, User, Password);
        var tree = await client.TreeConnectAsync("data");
        var slow = await client.OpenAsync(tree, "slow.bin");
        var fast = await client.OpenAsync(tree, "fast.bin");
        fs.GiveUp("slow.bin");

        var response = await client.ReadRawAsync(tree, slow, 0, SlowContent.Length).WaitAsync(Prompt);
        Assert.AreEqual(Status.IoTimeout, response.Status, $"0x{response.Status:X8}");
        CollectionAssert.AreEqual(FastContent, await client.ReadAsync(tree, fast, 0, FastContent.Length), "the connection carries on");
    }

    /// <summary>
    /// The same inside a compound chain, as clients send an open, a read and a close in one message: the READ whose
    /// backend call gives up is answered STATUS_IO_TIMEOUT, the CLOSE after it still closes the file, and the connection
    /// carries on.
    /// </summary>
    [TestMethod]
    public async Task ABackendThatGivesUpInsideAChainFailsItsCommandNotTheConnection()
    {
        var fs = new HeldFileSystem(_dir);
        await using var server = Start(fs);
        await using var client = await SmbTestClient.SignInAsync(server.LocalEndPoint, User, Password);
        var tree = await client.TreeConnectAsync("data");
        var fast = await client.OpenAsync(tree, "fast.bin");
        fs.GiveUp("slow.bin");

        var responses = await OpenReadCloseAsync(client, tree, "slow.bin");
        Assert.AreEqual(3, responses.Count, "a reply to each command of the chain");
        Assert.AreEqual(Status.Success, responses[0].Status, $"the CREATE: 0x{responses[0].Status:X8}");
        Assert.AreEqual(Status.IoTimeout, responses[1].Status, $"the READ: 0x{responses[1].Status:X8}");
        Assert.AreEqual(Status.Success, responses[2].Status, $"the CLOSE: 0x{responses[2].Status:X8}");
        Assert.AreEqual(1, fs.Closed, "the chain's CLOSE closed the file");
        CollectionAssert.AreEqual(FastContent, await client.ReadAsync(tree, fast, 0, FastContent.Length), "the connection carries on");
    }

    /// <summary>
    /// A chain whose CREATE gives up: the CREATE is answered STATUS_IO_TIMEOUT, and so are the commands after it that
    /// needed its file ([MS-SMB2] 3.3.5.2.7.2); the connection carries on.
    /// </summary>
    [TestMethod]
    public async Task AChainWhoseCreateGivesUpFailsTheCommandsOnItsFile()
    {
        var fs = new HeldFileSystem(_dir);
        await using var server = Start(fs);
        await using var client = await SmbTestClient.SignInAsync(server.LocalEndPoint, User, Password);
        var tree = await client.TreeConnectAsync("data");
        var fast = await client.OpenAsync(tree, "fast.bin");
        fs.GiveUpLookups("slow.bin");

        var responses = await OpenReadCloseAsync(client, tree, "slow.bin");
        Assert.AreEqual(3, responses.Count, "a reply to each command of the chain");
        foreach (var response in responses)
        {
            Assert.AreEqual(Status.IoTimeout, response.Status, $"{response.Command}: 0x{response.Status:X8}");
        }

        CollectionAssert.AreEqual(FastContent, await client.ReadAsync(tree, fast, 0, FastContent.Length), "the connection carries on");
    }

    /// <summary>
    /// A READ that reaches the end of the file asks the file system once. Clients read in big pieces (64 KiB of a 16 KiB
    /// file here), and a second call to hear of the end the file's length already gave would be a slow backend's second
    /// round trip for every small file read.
    /// </summary>
    [TestMethod]
    public async Task AReadToTheEndAsksTheFileSystemOnce()
    {
        var fs = new HeldFileSystem(_dir);
        await using var server = Start(fs);
        await using var client = await SmbTestClient.SignInAsync(server.LocalEndPoint, User, Password);
        var tree = await client.TreeConnectAsync("data");
        var fast = await client.OpenAsync(tree, "fast.bin");
        var before = fs.Reads;
        CollectionAssert.AreEqual(FastContent, await client.ReadAsync(tree, fast, 0, 64 << 10));
        Assert.AreEqual(1, fs.Reads - before, "the file system's reads for one READ that reaches the end");

        // a short read short of the end still asks again
        CollectionAssert.AreEqual(FastContent[100..], await client.ReadAsync(tree, fast, 100, 64 << 10));
    }

    /// <summary>
    /// A CLOSE asking for the attributes of an entry gone from under its open (renamed or deleted through another open
    /// meanwhile, likelier with a connection's requests side by side) is answered with what the open last saw of it. macOS's
    /// client asks on every CLOSE and calls the server broken both for empty times ("Bad SMB 2/3 Server, close attr of
    /// LastWriteTime is 0") and for none ("did not return close attributes").
    /// </summary>
    [TestMethod]
    public async Task ACloseOfAnEntryGoneFromUnderItsOpenGivesWhatItLastSaw()
    {
        await using var server = Start(new HeldFileSystem(_dir));
        await using var client = await SmbTestClient.SignInAsync(server.LocalEndPoint, User, Password);
        var tree = await client.TreeConnectAsync("data");
        var create = (await client.CreateAsync(tree, "docs", options: CreateOptions.DirectoryFile)).EnsureSuccess();
        var docs = SmbTestClient.FileIdOf(create);
        Directory.Delete(Path.Combine(_dir, "docs"));
        var gone = (await client.CloseAsync(tree, docs, postQuery: true)).EnsureSuccess();
        Assert.AreEqual(CloseFlags.PostQueryAttributes, Wire.U16(gone.Body, 2), "the CLOSE gave no attributes for an entry gone from under its open");
        Assert.AreEqual(Wire.U64(create.Body, 24), Wire.U64(gone.Body, 24), "the last write time the open last saw");
        Assert.AreNotEqual(0UL, Wire.U64(gone.Body, 24), "a last write time");
        Assert.AreEqual(Wire.U32(create.Body, 56), Wire.U32(gone.Body, 56), "the attributes the open last saw");

        var fast = await client.OpenAsync(tree, "fast.bin");
        var there = (await client.CloseAsync(tree, fast, postQuery: true)).EnsureSuccess();
        Assert.AreEqual(CloseFlags.PostQueryAttributes, Wire.U16(there.Body, 2), "an entry that is there has its attributes");
        Assert.AreNotEqual(0UL, Wire.U64(there.Body, 24), "a last write time");
        Assert.AreEqual((ulong)FastContent.Length, Wire.U64(there.Body, 48), "its length");
    }

    /// <summary>A CANCEL is never answered: not one that names nothing, nor one from a session that has not signed in.</summary>
    [TestMethod]
    public async Task ACancelIsNeverAnswered()
    {
        await using var server = Start(new HeldFileSystem(_dir));
        var connection = TestSupport.Connection(server);
        var cancel = TestSupport.Seg([4, 0, 0, 0]);
        Wire.Put16(cancel, 12, (ushort)Command.Cancel);
        Wire.Put64(cancel, 24, 7);
        Wire.Put64(cancel, 40, 12345); // no such session
        Assert.IsNull(await connection.ProcessAsync(cancel, TestSupport.None));

        Wire.Put32(cancel, 16, (uint)HeaderFlags.AsyncCommand);
        Wire.Put64(cancel, 32, 99);
        Assert.IsNull(await connection.ProcessAsync(cancel, TestSupport.None));
    }

    [TestMethod]
    public void NoRequestsAtOnceIsRefused()
        => Assert.ThrowsExactly<ArgumentException>(() => SmbServer.Start(new SmbServerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, 0), MaxConcurrentRequests = 0 }));

    [TestMethod]
    public void ANegativeInterimDelayIsRefused()
        => Assert.ThrowsExactly<ArgumentException>(() => SmbServer.Start(new SmbServerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, 0), InterimResponseDelay = TimeSpan.FromSeconds(-1) }));

    /// <summary>
    /// An open, a read of the whole file and a close as one compound chain of related commands; a
    /// <see cref="TimeoutException"/> when the chain is not answered (the connection dropped, say).
    /// </summary>
    private static async Task<List<SmbResponse>> OpenReadCloseAsync(SmbTestClient client, uint tree, string name)
    {
        try
        {
            return await client.SendCompoundAsync(
                [
                    (Command.Create, SmbTestClient.CreateBody(name, access: 0x00120089)),
                    (Command.Read, SmbTestClient.ReadBody(SmbTestClient.NoFileId, 0, SlowContent.Length)),
                    (Command.Close, SmbTestClient.CloseBody(SmbTestClient.NoFileId)),
                ],
                tree,
                related: true).WaitAsync(Prompt);
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException)
        {
            throw new TimeoutException($"the chain was not answered: {ex.Message}", ex);
        }
    }

    private static SmbServer Start(ISmbFileSystem fs, int maxConcurrentRequests = 64)
        => SmbServer.Start(new SmbServerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, 0), User = User, Password = Password, MaxConcurrentRequests = maxConcurrentRequests }, [new SmbShare("data", fs)]);
}
