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

    /// <summary>
    /// What goes for a held READ goes for the file system's other calls. A listing, a lookup and an open that wait on their
    /// backend hold up nothing else on the connection, are answered STATUS_PENDING meanwhile, and for real once let go.
    /// </summary>
    [TestMethod]
    [DataRow(HeldCall.Listing)]
    [DataRow(HeldCall.Lookup)]
    [DataRow(HeldCall.Open)]
    public async Task AHeldCallOfAnyKindHoldsUpNothingElseAndIsToldToWait(HeldCall call)
    {
        File.WriteAllText(Path.Combine(_dir, "docs", "a.txt"), "a");
        var fs = new HeldFileSystem(_dir);
        await using var server = Start(fs);
        await using var client = await SmbTestClient.SignInAsync(server.LocalEndPoint, User, Password);
        var tree = await client.TreeConnectAsync("data");
        var fast = await client.OpenAsync(tree, "fast.bin");
        var docs = await client.OpenAsync(tree, "docs", options: CreateOptions.DirectoryFile);
        var held = call == HeldCall.Listing ? "docs" : "slow.bin";
        fs.Hold(held, call);
        ulong request;
        try
        {
            request = await PostHeldAsync(client, tree, docs, call);
            await fs.Reached(held, call).WaitAsync(Prompt);
            var fastRead = await client.PostAsync(Command.Read, SmbTestClient.ReadBody(fast, 0, FastContent.Length), tree);

            // the other file's READ, and the word to the held request that it is still worked on, in whichever order
            var answered = new[] { await client.ReceiveAsync(Prompt), await client.ReceiveAsync(Prompt) }.ToDictionary(r => r.MessageId);
            Assert.IsTrue(answered.ContainsKey(fastRead) && answered.ContainsKey(request), "the READ of another file was answered, and the held request told to wait");
            CollectionAssert.AreEqual(FastContent, SmbTestClient.ReadData(answered[fastRead]));
            Assert.AreEqual(Status.Pending, answered[request].Status, $"0x{answered[request].Status:X8}");
        }
        finally
        {
            fs.Release(held, call);
        }

        var final = await client.ReceiveAsync(Prompt);
        Assert.AreEqual(request, final.MessageId);
        Assert.IsTrue(final.Async, "the real reply follows the interim one, asynchronous too");
        final.EnsureSuccess();
        if (call == HeldCall.Listing)
        {
            CollectionAssert.Contains(SmbTestClient.DirectoryNames(final, 0x25), "a.txt");
        }
    }

    /// <summary>A CANCEL ends a held listing, lookup or open as it does a held READ: the backend call is cancelled, the request answered STATUS_CANCELLED, and the connection carries on.</summary>
    [TestMethod]
    [DataRow(HeldCall.Listing)]
    [DataRow(HeldCall.Lookup)]
    [DataRow(HeldCall.Open)]
    public async Task ACancelEndsAHeldCallOfAnyKind(HeldCall call)
    {
        var fs = new HeldFileSystem(_dir);
        await using var server = Start(fs);
        await using var client = await SmbTestClient.SignInAsync(server.LocalEndPoint, User, Password);
        var tree = await client.TreeConnectAsync("data");
        var docs = await client.OpenAsync(tree, "docs", options: CreateOptions.DirectoryFile);
        var held = call == HeldCall.Listing ? "docs" : "slow.bin";
        fs.Hold(held, call);
        try
        {
            var request = await PostHeldAsync(client, tree, docs, call);
            await fs.Reached(held, call).WaitAsync(Prompt);
            await client.CancelAsync(request);
            var final = await client.ReceiveFinalAsync(Prompt);
            Assert.AreEqual(request, final.MessageId);
            Assert.AreEqual(Status.Cancelled, final.Status, $"0x{final.Status:X8}");
            Assert.AreEqual(1, fs.Cancelled, "the backend call was cancelled");
            Assert.AreEqual(0, fs.Closed, "nothing was opened, so nothing closed");
        }
        finally
        {
            fs.Release(held, call);
        }

        var fast = await client.OpenAsync(tree, "fast.bin");
        CollectionAssert.AreEqual(FastContent, await client.ReadAsync(tree, fast, 0, FastContent.Length), "the connection carries on");
    }

    /// <summary>
    /// A held call inside a compound chain (an open, a read and a close in one message, as clients send them) holds up
    /// nothing else either. The chain is not told to wait: the protocol allows that for a chain's last command alone, so
    /// its one reply comes when all of it is done.
    /// </summary>
    [TestMethod]
    public async Task AHeldCallInsideAChainHoldsUpNothingElse()
    {
        var fs = new HeldFileSystem(_dir);
        await using var server = Start(fs);
        await using var client = await SmbTestClient.SignInAsync(server.LocalEndPoint, User, Password);
        var tree = await client.TreeConnectAsync("data");
        var fast = await client.OpenAsync(tree, "fast.bin");
        fs.Hold("slow.bin", HeldCall.Open);
        ulong chain;
        try
        {
            chain = await client.PostCompoundAsync(
                [
                    (Command.Create, SmbTestClient.CreateBody("slow.bin", access: 0x00120089)),
                    (Command.Read, SmbTestClient.ReadBody(SmbTestClient.NoFileId, 0, SlowContent.Length)),
                    (Command.Close, SmbTestClient.CloseBody(SmbTestClient.NoFileId)),
                ],
                tree,
                related: true);
            await fs.Reached("slow.bin", HeldCall.Open).WaitAsync(Prompt);
            var fastRead = await client.PostAsync(Command.Read, SmbTestClient.ReadBody(fast, 0, FastContent.Length), tree);
            var answer = await client.ReceiveAsync(Prompt);
            Assert.AreEqual(fastRead, answer.MessageId, "the READ of another file was answered while the chain waited");
            CollectionAssert.AreEqual(FastContent, SmbTestClient.ReadData(answer));

            // long enough for an interim reply to have been sent, were the chain given one
            await Task.Delay(1000);
        }
        finally
        {
            fs.Release("slow.bin", HeldCall.Open);
        }

        var responses = await client.ReceiveCompoundAsync(Prompt);
        Assert.AreEqual(3, responses.Count, "the next message is the chain's whole reply");
        Assert.AreEqual(chain, responses[0].MessageId);
        foreach (var response in responses)
        {
            response.EnsureSuccess();
        }

        CollectionAssert.AreEqual(SlowContent, SmbTestClient.ReadData(responses[1]));
        Assert.AreEqual(1, fs.Closed, "the chain's CLOSE closed the file");
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
    /// With every place taken by held READs, a CANCEL of one of them is still read and acted on: it must not wait in the
    /// socket behind the very requests it is for.
    /// </summary>
    [TestMethod]
    public async Task ACancelReachesAHeldRequestWithEveryPlaceTaken()
    {
        var fs = new HeldFileSystem(_dir);
        await using var server = Start(fs, maxConcurrentRequests: 2);
        await using var client = await SmbTestClient.SignInAsync(server.LocalEndPoint, User, Password);
        var tree = await client.TreeConnectAsync("data");
        var slow = await client.OpenAsync(tree, "slow.bin");
        var fast = await client.OpenAsync(tree, "fast.bin");
        fs.Hold("slow.bin");
        fs.Hold("fast.bin");
        try
        {
            var slowRead = await client.PostAsync(Command.Read, SmbTestClient.ReadBody(slow, 0, SlowContent.Length), tree);
            await client.PostAsync(Command.Read, SmbTestClient.ReadBody(fast, 0, FastContent.Length), tree);
            await Task.WhenAll(fs.Reached("slow.bin"), fs.Reached("fast.bin")).WaitAsync(Prompt);
            await client.CancelAsync(slowRead);
            var final = await client.ReceiveFinalAsync(Prompt);
            Assert.AreEqual(slowRead, final.MessageId);
            Assert.AreEqual(Status.Cancelled, final.Status, $"0x{final.Status:X8}");
        }
        finally
        {
            fs.Release("slow.bin");
            fs.Release("fast.bin");
        }
    }

    /// <summary>
    /// With every place taken by held READs, an ECHO is still answered: a client sends one to see whether a server that
    /// has gone quiet is still there, and gives the connection up when it is not answered.
    /// </summary>
    [TestMethod]
    public async Task AnEchoIsAnsweredWithEveryPlaceTaken()
    {
        var fs = new HeldFileSystem(_dir);
        await using var server = Start(fs, maxConcurrentRequests: 2);
        await using var client = await SmbTestClient.SignInAsync(server.LocalEndPoint, User, Password);
        var tree = await client.TreeConnectAsync("data");
        var slow = await client.OpenAsync(tree, "slow.bin");
        var fast = await client.OpenAsync(tree, "fast.bin");
        fs.Hold("slow.bin");
        fs.Hold("fast.bin");
        try
        {
            await client.PostAsync(Command.Read, SmbTestClient.ReadBody(slow, 0, SlowContent.Length), tree);
            await client.PostAsync(Command.Read, SmbTestClient.ReadBody(fast, 0, FastContent.Length), tree);
            await Task.WhenAll(fs.Reached("slow.bin"), fs.Reached("fast.bin")).WaitAsync(Prompt);
            var echo = await client.PostAsync(Command.Echo, [4, 0, 0, 0]);
            var final = await client.ReceiveFinalAsync(Prompt);
            Assert.AreEqual(echo, final.MessageId);
            Assert.AreEqual(Status.Success, final.Status, $"0x{final.Status:X8}");
        }
        finally
        {
            fs.Release("slow.bin");
            fs.Release("fast.bin");
        }
    }

    /// <summary>
    /// A request waiting for a place (two held READs have both) is told so by an interim reply, like any request that
    /// takes long, and a CANCEL ends it there: it never reaches the file system.
    /// </summary>
    [TestMethod]
    public async Task ARequestWaitingForAPlaceIsToldSoAndCanBeCancelled()
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
            await client.PostAsync(Command.Read, SmbTestClient.ReadBody(slow, 0, SlowContent.Length), tree);
            await client.PostAsync(Command.Read, SmbTestClient.ReadBody(fast, 0, FastContent.Length), tree);
            await Task.WhenAll(fs.Reached("slow.bin"), fs.Reached("fast.bin")).WaitAsync(Prompt);
            var listing = await client.PostAsync(Command.QueryDirectory, SmbTestClient.QueryDirectoryBody(root, flags: QueryDirectoryFlags.RestartScans), tree);

            // the two held READs are told they are still worked on too
            SmbResponse interim;
            var deadline = DateTime.UtcNow + Prompt;
            do
            {
                interim = await client.ReceiveAsync(deadline - DateTime.UtcNow is { Ticks: > 0 } left ? left : TimeSpan.Zero);
                Assert.AreEqual(Status.Pending, interim.Status, $"nothing is answered for real yet: 0x{interim.Status:X8}");
            }
            while (interim.MessageId != listing);

            await client.CancelAsync(listing, interim.AsyncId);
            var final = await client.ReceiveFinalAsync(Prompt);
            Assert.AreEqual(listing, final.MessageId);
            Assert.AreEqual(Status.Cancelled, final.Status, $"0x{final.Status:X8}");
            Assert.AreEqual(0, fs.Listed, "the listing reached the file system");
        }
        finally
        {
            fs.Release("slow.bin");
            fs.Release("fast.bin");
        }
    }

    /// <summary>
    /// A READ waiting for its open's turn behind a held READ takes no place: with two places, another file's READ is
    /// still answered. Clients keep several reads of a file in flight, so a few slow files would otherwise take every
    /// place and hold up everything else on the connection.
    /// </summary>
    [TestMethod]
    public async Task ARequestWaitingForItsOpensTurnTakesNoPlace()
    {
        var fs = new HeldFileSystem(_dir);
        await using var server = Start(fs, maxConcurrentRequests: 2);
        await using var client = await SmbTestClient.SignInAsync(server.LocalEndPoint, User, Password);
        var tree = await client.TreeConnectAsync("data");
        var slow = await client.OpenAsync(tree, "slow.bin");
        var fast = await client.OpenAsync(tree, "fast.bin");
        const int half = 32 << 10;
        fs.Hold("slow.bin");
        try
        {
            await client.PostAsync(Command.Read, SmbTestClient.ReadBody(slow, 0, half), tree);
            await fs.Reached("slow.bin").WaitAsync(Prompt);
            await client.PostAsync(Command.Read, SmbTestClient.ReadBody(slow, half, half), tree);
            await Task.Delay(200);
            var fastRead = await client.PostAsync(Command.Read, SmbTestClient.ReadBody(fast, 0, FastContent.Length), tree);
            var final = await client.ReceiveFinalAsync(Prompt);
            Assert.AreEqual(fastRead, final.MessageId);
            CollectionAssert.AreEqual(FastContent, SmbTestClient.ReadData(final));
        }
        finally
        {
            fs.Release("slow.bin");
        }

        var halves = new List<byte[]> { SmbTestClient.ReadData(await client.ReceiveFinalAsync(Prompt)), SmbTestClient.ReadData(await client.ReceiveFinalAsync(Prompt)) };
        Assert.AreEqual(1, fs.MostAtOnce, "calls on one open overlapped");
        Assert.IsTrue(halves.Any(d => d.SequenceEqual(SlowContent[..half])) && halves.Any(d => d.SequenceEqual(SlowContent[half..])), "both halves read");
    }

    /// <summary>
    /// A READ whose reply waits for the wire takes no place. A client asks for the most a READ gives, 96 MiB of it in
    /// all, and reads none of it, so the replies back up from the socket; with two places, a listing still reaches the
    /// file system. And only as many of those replies as there are places are kept in memory: the READs behind them wait
    /// their turn, unread data and all, rather than each hold its buffer.
    /// </summary>
    [TestMethod]
    public async Task ARequestWhoseReplyWaitsForTheWireTakesNoPlace()
    {
        const int most = Smb2.MaxIoSize;
        const int reads = (96 << 20) / most;
        File.WriteAllBytes(Path.Combine(_dir, "big.bin"), new byte[most]);
        var fs = new HeldFileSystem(_dir);
        await using var server = Start(fs, maxConcurrentRequests: 2);
        await using var client = await SmbTestClient.SignInAsync(server.LocalEndPoint, User, Password);
        var tree = await client.TreeConnectAsync("data");
        var big = await client.OpenAsync(tree, "big.bin");
        var root = await client.OpenAsync(tree, "", options: CreateOptions.DirectoryFile);
        for (var i = 0; i < reads; i++)
        {
            await client.PostAsync(Command.Read, SmbTestClient.ReadBody(big, 0, most), tree);
        }

        // until no READ has reached the file system for a while: the wire is full, and so is the memory for replies
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        for (int seen = -1, quiet = 0; quiet < 5 && DateTime.UtcNow < deadline; quiet = fs.Reads == seen ? quiet + 1 : 0)
        {
            seen = fs.Reads;
            await Task.Delay(100);
        }

        var stalledAt = fs.Reads;
        Assert.IsLessThan(reads, stalledAt, "every READ was read into memory though the client took none of the replies");

        await client.PostAsync(Command.QueryDirectory, SmbTestClient.QueryDirectoryBody(root, flags: QueryDirectoryFlags.RestartScans), tree);
        deadline = DateTime.UtcNow + Prompt;
        while (fs.Listed == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.AreEqual(1, fs.Listed, "the listing reached the file system while READ replies waited for the wire");

        // the client reads at last: every READ and the listing are answered
        for (var i = 0; i < reads + 1; i++)
        {
            (await client.ReceiveFinalAsync(TimeSpan.FromSeconds(30))).EnsureSuccess();
        }
    }

    /// <summary>
    /// A client that sends more than may wait: with one place taken by a held READ and room for a few requests in the
    /// line, the rest of thirty READs is left in the socket, not read into memory; once the held READ is done every one
    /// of them is read and answered.
    /// </summary>
    [TestMethod]
    public async Task WhatMayWaitIsBoundedAndReadOnceThereIsRoom()
    {
        const int waitingBytes = 8 << 10;
        const int reads = 30;
        var fs = new HeldFileSystem(_dir);
        await using var server = Start(fs, maxConcurrentRequests: 1, waitingBytes: waitingBytes);
        await using var client = await SmbTestClient.SignInAsync(server.LocalEndPoint, User, Password);
        var tree = await client.TreeConnectAsync("data");
        var slow = await client.OpenAsync(tree, "slow.bin");
        var fast = await client.OpenAsync(tree, "fast.bin");
        var connection = server.Connections.Keys.Single();
        fs.Hold("slow.bin");
        try
        {
            await client.PostAsync(Command.Read, SmbTestClient.ReadBody(slow, 0, SlowContent.Length), tree);
            await fs.Reached("slow.bin").WaitAsync(Prompt);
            for (var i = 0; i < reads; i++)
            {
                await client.PostAsync(Command.Read, SmbTestClient.ReadBody(fast, 0, FastContent.Length), tree);
            }

            await Task.Delay(500);
            var waiting = connection.WaitingBytes;
            Assert.IsGreaterThanOrEqualTo(waitingBytes, waiting, "the line filled");
            Assert.IsLessThan(waitingBytes + (2 << 10), waiting, "no more was read than may wait, and the request that filled the line");
        }
        finally
        {
            fs.Release("slow.bin");
        }

        for (var i = 0; i < reads + 1; i++)
        {
            (await client.ReceiveFinalAsync(Prompt)).EnsureSuccess();
        }

        Assert.AreEqual(0, connection.WaitingBytes, "nothing waits any more");
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
    /// A client that goes away while its CLOSE waits its turn behind a held READ of the same open. The CLOSE took the open
    /// out of the connection's table, so no one else can close its file any more; it does, although its own wait was
    /// cancelled with everything else and the READ's backend call takes a moment to stop.
    /// </summary>
    [TestMethod]
    public async Task ACloseWaitingItsTurnWhenTheClientGoesStillClosesTheFile()
    {
        var fs = new HeldFileSystem(_dir) { StopDelay = TimeSpan.FromMilliseconds(100) };
        await using var server = Start(fs);
        var client = await SmbTestClient.SignInAsync(server.LocalEndPoint, User, Password);
        var tree = await client.TreeConnectAsync("data");
        var slow = await client.OpenAsync(tree, "slow.bin");
        fs.Hold("slow.bin");
        try
        {
            await client.PostAsync(Command.Read, SmbTestClient.ReadBody(slow, 0, SlowContent.Length), tree);
            await fs.Reached("slow.bin").WaitAsync(Prompt);
            await client.PostAsync(Command.Close, SmbTestClient.CloseBody(slow), tree);
            await Task.Delay(300);
            await client.DisposeAsync();

            var deadline = DateTime.UtcNow + Prompt;
            while ((fs.Closed == 0 || !server.Connections.IsEmpty) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            Assert.AreEqual(1, fs.Cancelled, "the held READ's backend call was cancelled");
            Assert.AreEqual(1, fs.Closed, "the file was closed");
            Assert.IsTrue(server.Connections.IsEmpty, "the connection was let go");
        }
        finally
        {
            fs.Release("slow.bin");
        }
    }

    /// <summary>
    /// A CLOSE cancelled while it waits its turn behind a held READ is not given up: nobody else could close the file. It
    /// is answered once the READ is done, and the file is closed.
    /// </summary>
    [TestMethod]
    public async Task ACloseCancelledWhileItWaitsItsTurnStillClosesTheFile()
    {
        var fs = new HeldFileSystem(_dir);
        await using var server = Start(fs);
        await using var client = await SmbTestClient.SignInAsync(server.LocalEndPoint, User, Password);
        var tree = await client.TreeConnectAsync("data");
        var slow = await client.OpenAsync(tree, "slow.bin");
        fs.Hold("slow.bin");
        ulong read, close;
        try
        {
            read = await client.PostAsync(Command.Read, SmbTestClient.ReadBody(slow, 0, SlowContent.Length), tree);
            await fs.Reached("slow.bin").WaitAsync(Prompt);
            close = await client.PostAsync(Command.Close, SmbTestClient.CloseBody(slow), tree);
            await Task.Delay(200);
            await client.CancelAsync(close);
            await Task.Delay(300);
            Assert.AreEqual(0, fs.Closed, "the file is not closed under the READ");
        }
        finally
        {
            fs.Release("slow.bin");
        }

        var answered = new[] { await client.ReceiveFinalAsync(Prompt), await client.ReceiveFinalAsync(Prompt) }.ToDictionary(r => r.MessageId);
        Assert.IsTrue(answered.ContainsKey(read) && answered.ContainsKey(close), "the READ and the CLOSE were answered");
        CollectionAssert.AreEqual(SlowContent, SmbTestClient.ReadData(answered[read]));
        Assert.AreEqual(1, fs.Closed, "the file was closed");
    }

    /// <summary>
    /// A CLOSE asking for the attributes of a file whose lookup gives up (a backend time-out) still closes the file, and
    /// answers with what the open last saw of it: an error would leave the client to send the CLOSE again, to a server
    /// that no longer knows the open, with the file left open behind it.
    /// </summary>
    [TestMethod]
    public async Task ACloseWhoseLookupGivesUpStillClosesTheFile()
    {
        var fs = new HeldFileSystem(_dir);
        await using var server = Start(fs);
        await using var client = await SmbTestClient.SignInAsync(server.LocalEndPoint, User, Password);
        var tree = await client.TreeConnectAsync("data");
        var create = (await client.CreateAsync(tree, "fast.bin", access: 0x00120089)).EnsureSuccess();
        fs.GiveUpLookups("fast.bin");

        var close = (await client.CloseAsync(tree, SmbTestClient.FileIdOf(create), postQuery: true).WaitAsync(Prompt)).EnsureSuccess();
        Assert.AreEqual(1, fs.Closed, "the file was closed");
        Assert.AreEqual(CloseFlags.PostQueryAttributes, Wire.U16(close.Body, 2), "the CLOSE gave no attributes");
        Assert.AreEqual(Wire.U64(create.Body, 24), Wire.U64(close.Body, 24), "the last write time the open last saw");
        Assert.AreEqual((ulong)FastContent.Length, Wire.U64(close.Body, 48), "its length");
    }

    /// <summary>
    /// A CREATE whose reply cannot be made (the lookup for it gives up) is answered STATUS_IO_TIMEOUT and leaves nothing
    /// open: no reply named the open, so no CLOSE would ever come for its file, which stayed open until the client's
    /// connection ended.
    /// </summary>
    [TestMethod]
    public async Task ACreateWhoseReplyGivesUpLeavesNoFileOpen()
    {
        var fs = new HeldFileSystem(_dir);
        await using var server = Start(fs);
        await using var client = await SmbTestClient.SignInAsync(server.LocalEndPoint, User, Password);
        var tree = await client.TreeConnectAsync("data");

        // the first lookup, whether the file is there, is answered; the one for the reply gives up
        fs.GiveUpLookups("fast.bin", after: 1);
        var create = await client.CreateAsync(tree, "fast.bin", access: 0x00120089).WaitAsync(Prompt);
        Assert.AreEqual(Status.IoTimeout, create.Status, $"0x{create.Status:X8}");
        Assert.AreEqual(1, fs.Closed, "the file the CREATE opened was closed");

        fs.AnswerLookups("fast.bin");
        var fast = await client.OpenAsync(tree, "fast.bin");
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

    /// <summary>The request that makes the held call, sent without waiting for its reply: a listing of docs, or an open of slow.bin (which looks it up first).</summary>
    private static Task<ulong> PostHeldAsync(SmbTestClient client, uint tree, byte[] docs, HeldCall call)
        => call == HeldCall.Listing
            ? client.PostAsync(Command.QueryDirectory, SmbTestClient.QueryDirectoryBody(docs, flags: QueryDirectoryFlags.RestartScans), tree)
            : client.PostAsync(Command.Create, SmbTestClient.CreateBody("slow.bin", access: 0x00120089), tree);

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

    private static SmbServer Start(ISmbFileSystem fs, int maxConcurrentRequests = 64, long? waitingBytes = null)
    {
        var options = new SmbServerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, 0), User = User, Password = Password, MaxConcurrentRequests = maxConcurrentRequests };
        if (waitingBytes is { } bytes)
        {
            options.WaitingBytes = bytes;
        }

        return SmbServer.Start(options, [new SmbShare("data", fs)]);
    }
}
