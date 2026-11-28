using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AnyFS.Mounts.Ftp;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;
using Microsoft.Extensions.Logging;

namespace AnyFS.Mounts.Tests;

/// <summary>Spec 8.5 and 16.1: the FTP provider in process, driven by a small FTP client over TcpClient (passive mode).</summary>
[TestClass]
public class FtpMountTests
{
    private static readonly byte[] Medium = Enumerable.Range(0, 100_000).Select(i => (byte)(i * 31 % 253)).ToArray();
    private readonly List<IAsyncDisposable> _cleanup = [];

    [TestCleanup]
    public async Task Cleanup()
    {
        foreach (var d in Enumerable.Reverse(_cleanup))
        {
            await d.DisposeAsync();
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static MemorySource Tree() => new MemorySource()
        .AddFile("readme.txt", "hello readme")
        .AddFile("docs/a.txt", "aaa")
        .AddFile("docs/sub/c.txt", "c")
        .AddFile("medium.bin", Medium, new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero))
        .AddFile("unicode/ünïcödé ✓ 日本語.txt", "unicode names")
        .AddDirectory("emptydir");

    private sealed record Collection(string Name) : ICollectionInfo
    {
        public Guid Id { get; } = Guid.NewGuid();
    }

    private async Task<(IMount Mount, TestMountContext Context, int Port)> StartAsync(ISource source, string collection = "work", int? port = null, TestMountContext? shareWith = null, bool readOnly = false, string? user = null, string? password = null, bool anonymous = true, Func<IVirtualFileSystem, IVirtualFileSystem>? wrap = null, ILogger? logger = null)
    {
        await source.InitialiseAsync(new TestSourceContext(collection), CancellationToken.None);
        var p = port ?? FreePort();
        var provider = new FtpMountProvider();
        var settings = (FtpMountSettings)provider.CreateDefaultSettings(new Collection(collection));
        settings.Port = p;
        settings.User = user;
        settings.Password = password;
        settings.AllowAnonymous = anonymous;
        var mount = provider.CreateMount(settings, new Collection(collection));
        var context = new TestMountContext(collection, "ftp", logger: logger ?? new ConsoleLogger(), shareWith: shareWith);
        Console.WriteLine($"[test] starting ftp mount '{collection}' on {p}");
        IVirtualFileSystem vfs = new SingleSourceVfs(source, collection, readOnly);
        await mount.StartAsync(wrap?.Invoke(vfs) ?? vfs, context, CancellationToken.None);
        Console.WriteLine($"[test] mount '{collection}' started");
        _cleanup.Add(mount);
        Assert.AreEqual($"ftp://{(user is null ? "" : user + "@")}localhost:{p}/{collection}/", mount.AccessPaths.Single().Value);
        Assert.IsInstanceOfType<MountOpenAction.Browser>(mount.OpenAction);
        return (mount, context, p);
    }

    private async Task<FtpClient> ConnectAsync(int port, string user = "anonymous", string password = "anyfs@test")
    {
        Console.WriteLine($"[test] connecting to {port} as {user}");
        var client = await FtpClient.ConnectAsync(port);
        _cleanup.Add(client);
        var reply = await client.LoginAsync(user, password);
        Console.WriteLine($"[test] login reply {reply.Code} {reply.Text}");
        Assert.AreEqual(230, reply.Code, reply.Text);
        return client;
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task AnonymousLoginListsAndReadsWithResume()
    {
        var (_, _, port) = await StartAsync(Tree());
        var client = await ConnectAsync(port);
        Console.WriteLine("[test] LIST /");
        var root = await client.ListAsync("/");
        CollectionAssert.Contains(root.Select(l => l.Name).ToList(), "work");

        Console.WriteLine("[test] LIST /work");
        var listing = await client.ListAsync("/work");
        var names = listing.Select(l => l.Name).ToList();
        CollectionAssert.Contains(names, "readme.txt");
        CollectionAssert.Contains(names, "docs");
        CollectionAssert.Contains(names, "emptydir");
        Assert.IsTrue(listing.Single(l => l.Name == "docs").IsDirectory);
        Assert.AreEqual(12, listing.Single(l => l.Name == "readme.txt").Size);

        Assert.AreEqual("hello readme", Encoding.UTF8.GetString(await client.RetrieveAsync("/work/readme.txt")));
        CollectionAssert.AreEqual(Medium, await client.RetrieveAsync("/work/medium.bin"));
        CollectionAssert.AreEqual(Medium[50_000..], await client.RetrieveAsync("/work/medium.bin", restart: 50_000), "REST resumes at the offset");
        Assert.AreEqual("unicode names", Encoding.UTF8.GetString(await client.RetrieveAsync("/work/unicode/ünïcödé ✓ 日本語.txt")));
        Assert.AreEqual(100_000, await client.SizeAsync("/work/medium.bin"));
        Assert.IsTrue((await client.RetrieveReplyAsync("/work/nope.txt")).Code >= 500, "a missing file is a 5xx");
    }

    /// <summary>curl, wget and lftp never send OPTS UTF8 ON; the lab found the unicode name coming back as question marks.</summary>
    [TestMethod]
    [Timeout(60000)]
    public async Task NamesAreUtf8ForAClientThatNeverAsks()
    {
        var (_, _, port) = await StartAsync(Tree());
        var client = await FtpClient.ConnectAsync(port, optsUtf8: false);
        Assert.AreEqual(230, (await client.LoginAsync("anonymous", "anyfs@test")).Code);
        var names = (await client.ListAsync("/work/unicode")).Select(l => l.Name).ToList();
        CollectionAssert.AreEqual(new[] { "ünïcödé ✓ 日本語.txt" }, names);
        Assert.AreEqual("unicode names", Encoding.UTF8.GetString(await client.RetrieveAsync("/work/unicode/ünïcödé ✓ 日本語.txt")));
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task WritesLandUpstreamAndRefusedWhenReadOnly()
    {
        var source = Tree();
        var (_, _, port) = await StartAsync(source);
        var client = await ConnectAsync(port);

        Assert.AreEqual(226, (await client.StoreAsync("/work/new.txt", Encoding.UTF8.GetBytes("stored"))).Code);
        Assert.AreEqual("stored", Encoding.UTF8.GetString(source.GetFile("new.txt")!));
        Assert.AreEqual(257, (await client.CommandAsync("MKD /work/made")).Code);
        Assert.IsTrue(source.Exists("made"));
        Assert.AreEqual(350, (await client.CommandAsync("RNFR /work/new.txt")).Code);
        Assert.AreEqual(250, (await client.CommandAsync("RNTO /work/made/renamed.txt")).Code);
        Assert.AreEqual("stored", Encoding.UTF8.GetString(source.GetFile("made/renamed.txt")!));
        Assert.AreEqual(250, (await client.CommandAsync("DELE /work/made/renamed.txt")).Code);
        Assert.IsFalse(source.Exists("made/renamed.txt"));
        Assert.AreEqual(250, (await client.CommandAsync("RMD /work/made")).Code);
        Assert.IsFalse(source.Exists("made"));
        Assert.IsTrue((await client.CommandAsync("RMD /work/docs")).Code >= 500, "a non-empty folder stays");
        Assert.IsTrue((await client.CommandAsync("MKD /elsewhere")).Code >= 500, "the server root takes no writes");

        var (_, _, roPort) = await StartAsync(Tree(), "ro", readOnly: true);
        var roClient = await ConnectAsync(roPort);
        Assert.IsTrue((await roClient.StoreAsync("/ro/new.txt", Encoding.UTF8.GetBytes("x"))).Code >= 500, "read-only mount refuses STOR");
        Assert.IsTrue((await roClient.CommandAsync("MKD /ro/d")).Code >= 500);
        Assert.IsTrue((await roClient.CommandAsync("DELE /ro/readme.txt")).Code >= 500);
        Assert.AreEqual("hello readme", Encoding.UTF8.GetString(await roClient.RetrieveAsync("/ro/readme.txt")), "reads still work");
        var perms = (await roClient.ListAsync("/ro")).Single(l => l.Name == "readme.txt").Permissions;
        Assert.IsFalse(perms.Contains('w'), $"no write bits in the listing: {perms}");
    }

    [TestMethod]
    [Timeout(60000)]
    public async Task NamedUsersSeeTheirCollectionsAndAnonymousTheOpenOnes()
    {
        var port = FreePort();
        var (_, context, _) = await StartAsync(Tree(), "private", port, user: "alice", password: "s3cret", anonymous: false);
        var (_, _, _) = await StartAsync(new MemorySource().AddFile("b.txt", "b"), "open", port, shareWith: context);
        Assert.AreEqual(1, context.SharedCount, "one FTP server for both");

        var alice = await ConnectAsync(port, "alice", "s3cret");
        var aliceSees = (await alice.ListAsync("/")).Select(l => l.Name).ToList();
        CollectionAssert.AreEqual(new[] { "private" }, aliceSees, "alice sees: " + string.Join(", ", aliceSees));
        Assert.AreEqual("hello readme", Encoding.UTF8.GetString(await alice.RetrieveAsync("/private/readme.txt")));

        var anonymous = await ConnectAsync(port);
        CollectionAssert.AreEqual(new[] { "open" }, (await anonymous.ListAsync("/")).Select(l => l.Name).ToList());
        Assert.IsTrue((await anonymous.RetrieveReplyAsync("/private/readme.txt")).Code >= 500, "anonymous can't reach the private collection");

        var wrong = await FtpClient.ConnectAsync(port);
        _cleanup.Add(wrong);
        Assert.AreEqual(530, (await wrong.LoginAsync("alice", "nope")).Code);
    }

    /// <summary>
    /// A login sees the collections whose own user name and password it gave. One collection's password showed every
    /// collection with that user name, whatever password each of them asks (the review of 21-Nov-2026, R14c).
    /// </summary>
    [TestMethod]
    [Timeout(60000)]
    public async Task ALoginSeesOnlyTheCollectionsWhosePasswordItGave()
    {
        var port = FreePort();
        var (_, context, _) = await StartAsync(Tree(), "mine", port, user: "alice", password: "s3cret", anonymous: false);
        var (_, _, _) = await StartAsync(new MemorySource().AddFile("t.txt", "theirs"), "theirs", port, shareWith: context, user: "alice", password: "an0ther", anonymous: false);

        var mine = await ConnectAsync(port, "alice", "s3cret");
        var seen = (await mine.ListAsync("/")).Select(l => l.Name).ToList();
        CollectionAssert.AreEqual(new[] { "mine" }, seen, "with the one password: " + string.Join(", ", seen));
        Assert.IsTrue((await mine.RetrieveReplyAsync("/theirs/t.txt")).Code >= 500, "and reaches nothing of the other");
        Assert.AreEqual("hello readme", Encoding.UTF8.GetString(await mine.RetrieveAsync("/mine/readme.txt")));

        var theirs = await ConnectAsync(port, "alice", "an0ther");
        seen = (await theirs.ListAsync("/")).Select(l => l.Name).ToList();
        CollectionAssert.AreEqual(new[] { "theirs" }, seen, "with the other: " + string.Join(", ", seen));
        Assert.AreEqual("theirs", Encoding.UTF8.GetString(await theirs.RetrieveAsync("/theirs/t.txt")));

        // collections that ask the same of a login are seen together, as before
        var (_, _, _) = await StartAsync(new MemorySource().AddFile("s.txt", "s"), "shared", port, shareWith: context, user: "alice", password: "s3cret", anonymous: false);
        var both = await ConnectAsync(port, "alice", "s3cret");
        seen = (await both.ListAsync("/")).Select(l => l.Name).ToList();
        CollectionAssert.AreEqual(new[] { "mine", "shared" }, seen, "two with one login: " + string.Join(", ", seen));

        var wrong = await FtpClient.ConnectAsync(port);
        _cleanup.Add(wrong);
        Assert.AreEqual(530, (await wrong.LoginAsync("alice", "nope")).Code, "a password that is no collection's");
    }

    /// <summary>Active mode (PORT: the server connects back to the client), MLSD facts, MDTM, and the modern-client dance of FEAT and OPTS.</summary>
    [TestMethod]
    [Timeout(60000)]
    public async Task ActiveModeMlsdAndFacts()
    {
        var (_, _, port) = await StartAsync(Tree());
        var client = await ConnectAsync(port);
        var feat = await client.CommandAsync("FEAT");
        Assert.AreEqual(211, feat.Code, feat.Text);
        StringAssert.Contains(feat.Text, "UTF8");
        StringAssert.Contains(feat.Text, "REST STREAM");
        StringAssert.Contains(feat.Text, "MLST");

        var facts = await client.MlsdAsync("/work");
        var medium = facts.Single(f => f.EndsWith(" medium.bin", StringComparison.Ordinal));
        StringAssert.Contains(medium, "type=file;");
        StringAssert.Contains(medium, "size=100000;");
        StringAssert.Contains(medium, "modify=20261001120000;");
        Assert.IsTrue(facts.Any(f => f.Contains("type=dir;", StringComparison.Ordinal) && f.EndsWith(" docs", StringComparison.Ordinal)), string.Join(" | ", facts));
        Assert.AreEqual("20261001120000", (await client.CommandAsync("MDTM /work/medium.bin")).Text.Trim());

        // active mode: our own listener, PORT, then LIST and RETR arrive on it
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var activePort = ((IPEndPoint)listener.LocalEndpoint).Port;
        Assert.AreEqual(200, (await client.CommandAsync($"PORT 127,0,0,1,{activePort / 256},{activePort % 256}")).Code);
        var names = await client.ListNamesActiveAsync("/work", listener);
        CollectionAssert.Contains(names, "readme.txt");
        Assert.AreEqual(200, (await client.CommandAsync($"PORT 127,0,0,1,{activePort / 256},{activePort % 256}")).Code);
        CollectionAssert.AreEqual(Medium, await client.RetrieveActiveAsync("/work/medium.bin", listener));

        // a data address that is not the client's own is refused (no bounce attacks)
        var bounce = await client.CommandAsync("PORT 10,1,2,3,200,10");
        Assert.IsTrue(bounce.Code >= 500, $"{bounce.Code} {bounce.Text}");
    }

    /// <summary>Eight sessions reading the same file at once all get it whole.</summary>
    [TestMethod]
    [Timeout(120000)]
    public async Task ConcurrentReadersGetTheWholeFile()
    {
        var big = new byte[4 << 20];
        new Random(5).NextBytes(big);
        var (_, _, port) = await StartAsync(new MemorySource().AddFile("big.bin", big));
        var reads = Enumerable.Range(0, 8).Select(async _ =>
        {
            var client = await FtpClient.ConnectAsync(port);
            _cleanup.Add(client);
            Assert.AreEqual(230, (await client.LoginAsync("anonymous", "x@y")).Code);
            return await client.RetrieveAsync("/work/big.bin");
        }).ToList();
        var results = await Task.WhenAll(reads);
        foreach (var result in results)
        {
            CollectionAssert.AreEqual(big, result);
        }
    }

    /// <summary>APPE adds to a file, REST before STOR writes from an offset, and a read-only membership refuses both.</summary>
    [TestMethod]
    [Timeout(60000)]
    public async Task AppendAndRestartedUploads()
    {
        var source = Tree();
        var (_, _, port) = await StartAsync(source);
        var client = await ConnectAsync(port);
        Assert.AreEqual(226, (await client.UploadAsync("/work/log.txt", Encoding.UTF8.GetBytes("one"), "STOR")).Code);
        Assert.AreEqual(226, (await client.UploadAsync("/work/log.txt", Encoding.UTF8.GetBytes("two"), "APPE")).Code);
        Assert.AreEqual("onetwo", Encoding.UTF8.GetString(source.GetFile("log.txt")!));
        Assert.AreEqual(350, (await client.CommandAsync("REST 3")).Code);
        Assert.AreEqual(226, (await client.UploadAsync("/work/log.txt", Encoding.UTF8.GetBytes("TWO"), "STOR")).Code);
        Assert.AreEqual("oneTWO", Encoding.UTF8.GetString(source.GetFile("log.txt")!), "REST then STOR overwrites from the offset");
        Assert.IsTrue((await client.UploadAsync("/log.txt", Encoding.UTF8.GetBytes("x"), "STOR")).Code >= 500, "the root takes no files");
    }

    /// <summary>
    /// A REST offset is for the transfer that follows it and for nothing else. It outlived a transfer that was refused
    /// before it began, and any command that is no transfer, so the next file came or went from part way in (the review
    /// of 21-Nov-2026, N16). The commands that only set a transfer up may come between, as they did.
    /// </summary>
    [TestMethod]
    [Timeout(60000)]
    public async Task ARestOffsetIsForTheTransferThatFollowsItAndForNoOther()
    {
        var source = Tree();
        var (_, _, port) = await StartAsync(source);
        var client = await ConnectAsync(port);

        // a transfer that is refused before it begins takes the offset with it
        Assert.AreEqual(350, (await client.CommandAsync("REST 50000")).Code);
        Assert.AreEqual(550, (await client.CommandAsync("RETR /work/nope.bin")).Code);
        CollectionAssert.AreEqual(Medium, await client.RetrieveAsync("/work/medium.bin"), "after a RETR of no such file, the next file comes whole");

        Assert.AreEqual(350, (await client.CommandAsync("REST 50000")).Code);
        Assert.AreEqual(425, (await client.CommandAsync("RETR /work/medium.bin")).Code);
        CollectionAssert.AreEqual(Medium, await client.RetrieveAsync("/work/medium.bin"), "after a RETR with no data connection to go over");

        Assert.AreEqual(350, (await client.CommandAsync("REST 3")).Code);
        Assert.IsTrue((await client.UploadAsync("/nowhere.txt", Encoding.UTF8.GetBytes("x"), "STOR")).Code >= 500);
        Assert.AreEqual(226, (await client.StoreAsync("/work/readme.txt", Encoding.UTF8.GetBytes("new"))).Code);
        Assert.AreEqual("new", Encoding.UTF8.GetString(source.GetFile("readme.txt")!), "after a STOR that was refused, the next file is written from its start");

        // and any other command that comes after it forgets it
        foreach (var between in new[] { "CWD /work", "SIZE /work/medium.bin", "NOOP", "NLST /work/nope" })
        {
            Assert.AreEqual(350, (await client.CommandAsync("REST 50000")).Code);
            await client.CommandAsync(between);
            CollectionAssert.AreEqual(Medium, await client.RetrieveAsync("/work/medium.bin"), $"{between} is no transfer the offset was for");
        }

        // TYPE and PASV, which this client sends before each RETR, only set the transfer up: the offset is still its own
        Assert.AreEqual(350, (await client.CommandAsync("REST 50000")).Code);
        CollectionAssert.AreEqual(Medium[50_000..], await client.RetrieveAsync("/work/medium.bin"), "REST, then TYPE and PASV, then RETR");
        CollectionAssert.AreEqual(Medium, await client.RetrieveAsync("/work/medium.bin"), "and the transfer after that one is whole");
    }

    /// <summary>ABOR during a download: the transfer stops with 426 then 226 and the session carries on.</summary>
    [TestMethod]
    [Timeout(60000)]
    public async Task AbortStopsATransferAndTheSessionGoesOn()
    {
        var (_, _, port) = await StartAsync(new MemorySource().AddFile("big.bin", new byte[64 << 20]));
        var client = await ConnectAsync(port);
        using var data = await client.PassiveAsync();
        var reply = await client.CommandAsync("RETR /work/big.bin");
        Assert.AreEqual(150, reply.Code, reply.Text);
        // read a little, then stop reading: the server's writes back up and ABOR has something to abort
        var some = new byte[65536];
        Assert.IsTrue(await data.GetStream().ReadAsync(some) > 0);
        await Task.Delay(300);
        await client.SendAsync("ABOR");
        var first = await client.ReadReplyAsync();
        var second = first.Code == 426 ? await client.ReadReplyAsync() : first;
        Assert.AreEqual(426, first.Code, first.Text);
        Assert.AreEqual(226, second.Code, second.Text);
        data.Close();
        Assert.AreEqual(200, (await client.CommandAsync("NOOP")).Code, "the control connection is still good");
        Assert.IsTrue((await client.ListAsync("/work")).Count > 0, "and still lists");
    }

    /// <summary>
    /// An upload that is aborted, or whose data connection breaks, is no file. The part that came was closed as the file,
    /// which a source that takes a file at its close (SharePoint's upload) put in place of the one that was there (review of
    /// 21-Nov-2026, R3): it is given up now. One that came whole is closed, before the client is told it is complete.
    /// </summary>
    [TestMethod]
    public async Task AnUploadAbortedOrBrokenOffIsGivenUpNotKeptAsFarAsItCame()
    {
        ClosesTold told = null!;
        var (_, _, port) = await StartAsync(Tree(), wrap: vfs => told = new ClosesTold(vfs));
        var client = await ConnectAsync(port);

        using (var data = await client.PassiveAsync())
        {
            Assert.AreEqual(150, (await client.CommandAsync("STOR /work/aborted.bin")).Code);
            await data.GetStream().WriteAsync(new byte[200_000]);
            await data.GetStream().FlushAsync();
            await Task.Delay(300);
            await client.SendAsync("ABOR");
            var first = await client.ReadReplyAsync();
            Assert.AreEqual(426, first.Code, first.Text);
            Assert.AreEqual(226, (await client.ReadReplyAsync()).Code);
        }

        CollectionAssert.AreEqual(new[] { "given up" }, told.Of("aborted.bin"), "aborted: " + string.Join(", ", told.Of("aborted.bin")));

        using (var data = await client.PassiveAsync())
        {
            Assert.AreEqual(150, (await client.CommandAsync("STOR /work/reset.bin")).Code);
            await data.GetStream().WriteAsync(new byte[200_000]);
            await data.GetStream().FlushAsync();
            await Task.Delay(300);
            data.Client.LingerState = new LingerOption(true, 0);   // closed with a reset, as a connection that broke
            data.Client.Close();
            var reply = await client.ReadReplyAsync();
            Assert.AreEqual(426, reply.Code, reply.Text);
        }

        CollectionAssert.AreEqual(new[] { "given up" }, told.Of("reset.bin"), "broken off: " + string.Join(", ", told.Of("reset.bin")));

        Assert.AreEqual(226, (await client.StoreAsync("/work/whole.bin", new byte[200_000])).Code);
        CollectionAssert.AreEqual(new[] { "closed" }, told.Of("whole.bin"), "whole: " + string.Join(", ", told.Of("whole.bin")));
        Assert.AreEqual(200, (await client.CommandAsync("NOOP")).Code, "the control connection is still good");
    }

    /// <summary>
    /// A command that fails in a way no handler expected is answered and logged, and the session goes on. A source's own
    /// fault (an I/O error, a call it gave up, a bug) ended the session with no reply, and for the first two with no line
    /// in the log that a person is shown; an I/O error of the source's part way through a transfer was taken for the data
    /// connection's (the review of 21-Nov-2026, R14c).
    /// </summary>
    [TestMethod]
    [Timeout(60000)]
    public async Task AnExceptionNoHandlerExpectedIsAnsweredAndLoggedAndTheSessionGoesOn()
    {
        var log = new Recorded();
        var source = Tree().AddFile("bug.bin", new byte[3 << 20]).AddFile("io.bin", new byte[3 << 20]).AddFile("late.txt", "late");
        var (_, _, port) = await StartAsync(source, logger: log, wrap: vfs => new Faulty(vfs, (call, path, offset) => (call, path) switch
        {
            ("stat", "io.txt") => new IOException("the disk said no"),
            ("stat", "given-up.txt") => new TaskCanceledException("the source gave the call up"),
            ("stat", "bug.txt") => new InvalidOperationException("a bug in the source"),
            ("read", "bug.bin") when offset >= 2 << 20 => new InvalidOperationException("a bug part way through"),
            ("read", "io.bin") when offset >= 2 << 20 => new IOException("the source's own connection broke"),
            ("close", "late.txt") => new InvalidOperationException("a bug at the close"),
            _ => null,
        }));
        var client = await ConnectAsync(port);

        void Warned(int before, string command, Type thrown)
        {
            var warnings = log.Warnings;
            Assert.AreEqual(before + 1, warnings.Count, $"{command}: one warning, and no other: {string.Join(" | ", warnings.Skip(before).Select(w => w.Message))}");
            var (level, message, exception) = warnings[^1];
            Assert.AreEqual(LogLevel.Warning, level);
            StringAssert.Contains(message, command, "the line names the command");
            Assert.IsInstanceOfType(exception, thrown, "with what was thrown");
            Assert.IsFalse(string.IsNullOrEmpty(exception.StackTrace), "and where");
        }

        foreach (var (name, thrown) in new[] { ("io.txt", typeof(IOException)), ("given-up.txt", typeof(TaskCanceledException)), ("bug.txt", typeof(InvalidOperationException)) })
        {
            var before = log.Warnings.Count;
            var reply = await client.CommandAsync("SIZE /work/" + name);
            Assert.AreEqual(451, reply.Code, reply.Text);
            Warned(before, "SIZE /work/" + name, thrown);
            Assert.AreEqual(200, (await client.CommandAsync("NOOP")).Code, "the session goes on");
        }

        // a transfer that fails before it begins is answered the same, with no 150 before it
        var count = log.Warnings.Count;
        Assert.AreEqual(451, (await client.RetrieveReplyAsync("/work/io.txt")).Code);
        Warned(count, "RETR /work/io.txt", typeof(IOException));

        // one that fails part way has had its 150: the 451 ends it, and the session transfers again. So with an I/O error
        // of the source's, which looks like the data connection's own and was told as that: a 426, and nothing in the log
        foreach (var (name, thrown) in new[] { ("bug.bin", typeof(InvalidOperationException)), ("io.bin", typeof(IOException)) })
        {
            count = log.Warnings.Count;
            using (var data = await client.PassiveAsync())
            {
                Assert.AreEqual(150, (await client.CommandAsync("RETR /work/" + name)).Code);
                await data.GetStream().CopyToAsync(Stream.Null);
                var ended = await client.ReadReplyAsync();
                Assert.AreEqual(451, ended.Code, ended.Text);
            }

            Warned(count, "RETR /work/" + name, thrown);
            Assert.AreEqual("hello readme", Encoding.UTF8.GetString(await client.RetrieveAsync("/work/readme.txt")));
        }

        // one whose file fails to close when every byte has been sent is complete all the same, and the failure is logged
        count = log.Warnings.Count;
        Assert.AreEqual("late", Encoding.UTF8.GetString(await client.RetrieveAsync("/work/late.txt")));
        Assert.AreEqual(200, (await client.CommandAsync("NOOP")).Code, "the next command's reply is its own");
        Warned(count, "RETR /work/late.txt", typeof(InvalidOperationException));
    }

    /// <summary>
    /// The control connection breaking is no surprise of that kind. Its session ends with nothing in the log that a person
    /// is shown, as it did: whether a reply could not be written, or the next command could not be read during a transfer.
    /// </summary>
    [TestMethod]
    [Timeout(60000)]
    public async Task ASessionWhoseControlConnectionBreaksEndsWithNoWarning()
    {
        var log = new Recorded();
        var reached = new SemaphoreSlim(0);
        var held = new ManualResetEventSlim();
        var (_, _, port) = await StartAsync(Tree().AddFile("big.bin", new byte[64 << 20]), logger: log, wrap: vfs => new Faulty(vfs, (call, path, _) =>
        {
            if (call == "stat" && path == "docs/a.txt")
            {
                reached.Release();
                held.Wait(TimeSpan.FromSeconds(30));
            }

            return null;
        }));

        async Task ClosedAsync(IPEndPoint session)
        {
            var until = System.Diagnostics.Stopwatch.StartNew();
            while (!log.Lines.Any(l => l.Message == $"ftp {session}: closed") && until.Elapsed < TimeSpan.FromSeconds(15))
            {
                await Task.Delay(50);
            }

            Assert.IsTrue(log.Lines.Any(l => l.Message == $"ftp {session}: closed"), "the session ended");
        }

        // the client goes while its command is in hand: the reply has nowhere to go
        var gone = await FtpClient.ConnectAsync(port);
        Assert.AreEqual(230, (await gone.LoginAsync("anonymous", "x@y")).Code);
        var session = gone.Local;
        await gone.SendAsync("SIZE /work/docs/a.txt");
        Assert.IsTrue(await reached.WaitAsync(TimeSpan.FromSeconds(15)), "the command is in hand");
        gone.Reset();
        await Task.Delay(300);
        held.Set();
        await ClosedAsync(session);

        // the client goes during a transfer: the control connection is read all the while, and that read fails
        var cut = await FtpClient.ConnectAsync(port);
        Assert.AreEqual(230, (await cut.LoginAsync("anonymous", "x@y")).Code);
        session = cut.Local;
        using (var data = await cut.PassiveAsync())
        {
            Assert.AreEqual(150, (await cut.CommandAsync("RETR /work/big.bin")).Code);
            Assert.IsTrue(await data.GetStream().ReadAsync(new byte[65536]) > 0);
            cut.Reset();
            await ClosedAsync(session);
        }

        Assert.AreEqual(0, log.Warnings.Count, string.Join(" | ", log.Warnings.Select(w => w.Message)));
    }

    /// <summary>
    /// A command line over the limit (8,192 bytes) is answered 500, the rest of it is read to its end and dropped, and the
    /// session goes on, during a transfer as well. The limit was met by an exception that nothing answered: the session
    /// ended with no reply, and with a warning in the log (seen in the review of 21-Nov-2026 and left).
    /// </summary>
    [TestMethod]
    [Timeout(60000)]
    public async Task ALineTooLongIsAnswered500AndDroppedAndTheSessionGoesOn()
    {
        var reached = new SemaphoreSlim(0);
        var held = new ManualResetEventSlim();
        var (_, _, port) = await StartAsync(Tree().AddFile("held.bin", Medium), wrap: vfs => new Faulty(vfs, (call, path, offset) =>
        {
            if (call == "read" && path == "held.bin" && offset == 0)
            {
                reached.Release();
                held.Wait(TimeSpan.FromSeconds(30));
            }

            return null;
        }));
        var client = await ConnectAsync(port);

        // a line within the limit is a command like any other
        Assert.AreEqual(200, (await client.CommandAsync("NOOP " + new string('x', 8000))).Code);

        // a megabyte, and a command after it in the same write: the long line is answered once, and the command after it
        // has its own reply. Kept, the rest of the line would have grown without end, or been read as commands of its own
        FtpClient.Reply? tooLong = null;
        try
        {
            await client.SendAsync("NOOP " + new string('x', 1 << 20) + "\r\nNOOP");
            tooLong = await client.ReadReplyAsync();
        }
        catch (IOException ex)
        {
            Assert.Fail("the session ended, with no reply to the line too long: " + ex.Message);
        }

        Assert.AreEqual(500, tooLong?.Code, tooLong?.Text);
        Assert.AreEqual(200, (await client.ReadReplyAsync()).Code, "the command after it is read as itself");
        CollectionAssert.AreEqual(Medium, await client.RetrieveAsync("/work/medium.bin"), "and the session goes on");

        // during a transfer: answered, and the transfer goes on to its end
        using (var data = await client.PassiveAsync())
        {
            Assert.AreEqual(150, (await client.CommandAsync("RETR /work/held.bin")).Code);
            Assert.IsTrue(await reached.WaitAsync(TimeSpan.FromSeconds(15)), "the transfer is under way");
            await client.SendAsync("NOOP " + new string('x', 20_000));
            var during = await client.ReadReplyAsync();
            Assert.AreEqual(500, during.Code, during.Text);
            held.Set();
            using var came = new MemoryStream();
            await data.GetStream().CopyToAsync(came);
            CollectionAssert.AreEqual(Medium, came.ToArray(), "the file came whole");
            var done = await client.ReadReplyAsync();
            Assert.AreEqual(226, done.Code, done.Text);
        }

        Assert.AreEqual(200, (await client.CommandAsync("NOOP")).Code, "the session goes on after the transfer");
    }

    /// <summary>
    /// A transfer has one final reply, whatever its file's close does. A download's file was closed after its 226, and a
    /// close that failed with an error of the file system's was answered as well: a second reply, which the client read as
    /// its next command's (seen in the review of 21-Nov-2026 and left). A download whose every byte was sent is complete
    /// whatever its close does, and the failure is logged; a file written through a transfer has a failed close for its
    /// answer, STOR and APPE alike.
    /// </summary>
    [TestMethod]
    [Timeout(60000)]
    public async Task ATransferHasOneFinalReplyWhenItsFileFailsToClose()
    {
        var log = new Recorded();
        var (_, _, port) = await StartAsync(Tree().AddFile("late.bin", Medium), logger: log, wrap: vfs => new Faulty(vfs, (call, path, _)
            => call == "close" && path is "late.bin" or "written.txt" ? VfsException.Unavailable("the source went away at the close") : null));
        var client = await ConnectAsync(port);

        var before = log.Warnings.Count;
        CollectionAssert.AreEqual(Medium, await client.RetrieveAsync("/work/late.bin"), "every byte came, and the transfer is complete");
        var next = await client.CommandAsync("NOOP");
        Assert.AreEqual(200, next.Code, "the next command's reply is its own: " + next.Text);
        var warnings = log.Warnings.Skip(before).ToList();
        Assert.AreEqual(1, warnings.Count, "the close's failure is logged: " + string.Join(" | ", warnings.Select(w => w.Message)));
        StringAssert.Contains(warnings[0].Message, "RETR /work/late.bin", "the line names the command");
        Assert.IsInstanceOfType<VfsException>(warnings[0].Exception, "with what was thrown");

        foreach (var command in new[] { "STOR", "APPE" })
        {
            var reply = await client.UploadAsync("/work/written.txt", Encoding.UTF8.GetBytes("written"), command);
            Assert.AreEqual(450, reply.Code, $"{command}: the close's failure is the transfer's answer: {reply.Text}");
            next = await client.CommandAsync("NOOP");
            Assert.AreEqual(200, next.Code, $"after {command}, the next command's reply is its own: {next.Text}");
        }
    }

    /// <summary>
    /// The first command after a transfer is waited for as long as any other: a session that says nothing for the idle
    /// timeout is told so and closed. The read a transfer left going was waited on with no timeout at all, so a client that
    /// went quiet after a download or a listing held its session open for ever (seen in the review of 21-Nov-2026 and left).
    /// </summary>
    [TestMethod]
    [Timeout(60000)]
    public async Task TheFirstCommandAfterATransferIsWaitedForNoLongerThanAnyOther()
    {
        var (mount, _, port) = await StartAsync(Tree());
        ((FtpMount)mount).Server!.IdleTimeout = TimeSpan.FromSeconds(1);

        // what the server says unasked within ten seconds, or null
        static async Task<FtpClient.Reply?> UnaskedAsync(FtpClient client)
        {
            try
            {
                return await client.ReadReplyAsync(TimeSpan.FromSeconds(10));
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        // quiet after a command: told, and closed, as before
        var quiet = await ConnectAsync(port);
        Assert.AreEqual(200, (await quiet.CommandAsync("NOOP")).Code);
        var told = await UnaskedAsync(quiet);
        Assert.AreEqual(421, told?.Code, "quiet after a NOOP: " + told?.Text);

        // quiet after a download, and after a listing: the same
        foreach (var (what, transfer) in new (string, Func<FtpClient, Task>)[] { ("a download", c => c.RetrieveAsync("/work/readme.txt")), ("a listing", c => c.ListAsync("/work")) })
        {
            var after = await ConnectAsync(port);
            await transfer(after);
            told = await UnaskedAsync(after);
            Assert.IsNotNull(told, $"quiet after {what}: nothing said in ten seconds, the next command waited for with no timeout");
            Assert.AreEqual(421, told.Code, $"quiet after {what}: {told.Text}");
            StringAssert.StartsWith(told.Text, "Idle timeout", what);
            await Assert.ThrowsAsync<IOException>(() => after.ReadReplyAsync(), $"after {what}, the session is closed");
        }
    }

    [TestMethod]
    public void SettingsValidation()
    {
        var provider = new FtpMountProvider();
        Assert.AreEqual(0, provider.Validate(new FtpMountSettings()).Count());
        Assert.IsTrue(provider.Validate(new FtpMountSettings { AllowAnonymous = false }).Any(m => m.IsError), "nobody could log in");
        Assert.IsTrue(provider.Validate(new FtpMountSettings { User = "u" }).Any(m => m.IsError && m.Property == nameof(FtpMountSettings.Password)));
        Assert.IsTrue(provider.Validate(new FtpMountSettings { PassivePortFrom = 50000 }).Any(m => m.IsError), "half a range");
        Assert.IsTrue(provider.Validate(new FtpMountSettings { PassivePortFrom = 50010, PassivePortTo = 50000 }).Any(m => m.IsError), "upside down");
        Assert.IsTrue(provider.Validate(new FtpMountSettings { BindAddress = "0.0.0.0" }).Any(m => !m.IsError), "anonymous on the LAN warns");
        Assert.IsTrue(provider.Validate(new FtpMountSettings { PublicAddress = "not-an-ip" }).Any(m => m.IsError));
    }

    /// <summary>The server's log lines into the test output.</summary>
    private sealed class ConsoleLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Console.WriteLine($"[server {logLevel}] {formatter(state, exception)}{(exception is null ? "" : " " + exception)}");
    }

    /// <summary>The server's log lines kept for a test to read, and into the test output as well.</summary>
    private sealed class Recorded : ILogger
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> _lines = new();
        private readonly ConsoleLogger _console = new();

        public List<(LogLevel Level, string Message, Exception? Exception)> Lines => _lines.ToList();

        /// <summary>The lines a person is shown without asking for more: warnings and worse.</summary>
        public List<(LogLevel Level, string Message, Exception? Exception)> Warnings => _lines.Where(l => l.Level >= LogLevel.Warning).ToList();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            _lines.Enqueue((logLevel, formatter(state, exception), exception));
            _console.Log(logLevel, eventId, state, exception, formatter);
        }
    }

    /// <summary>Just enough FTP (RFC 959) to exercise the server: passive mode, LIST, RETR with REST, STOR, SIZE and the simple commands.</summary>
    private sealed class FtpClient : IAsyncDisposable
    {
        private readonly TcpClient _control;
        private readonly StreamReader _reader;
        private readonly StreamWriter _writer;

        private FtpClient(TcpClient control)
        {
            _control = control;
            var stream = control.GetStream();
            _reader = new StreamReader(stream, new UTF8Encoding(false));
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\r\n" };
        }

        public sealed record Reply(int Code, string Text);

        public sealed record Line(string Name, bool IsDirectory, long Size, string Permissions);

        /// <param name="optsUtf8">False for a client like curl, which never sends OPTS UTF8 ON and expects UTF-8 regardless.</param>
        public static async Task<FtpClient> ConnectAsync(int port, bool optsUtf8 = true)
        {
            var control = new TcpClient();
            await control.ConnectAsync(IPAddress.Loopback, port);
            var client = new FtpClient(control);
            var banner = await client.ReadReplyAsync();
            Assert.AreEqual(220, banner.Code, banner.Text);
            if (optsUtf8)
            {
                await client.CommandAsync("OPTS UTF8 ON");
            }

            return client;
        }

        public async Task<Reply> LoginAsync(string user, string password)
        {
            var reply = await CommandAsync("USER " + user);
            return reply.Code == 331 ? await CommandAsync("PASS " + password) : reply;
        }

        /// <summary>A command as the test output shows it: a password hidden, a long line cut short.</summary>
        private static string Shown(string command)
            => command.StartsWith("PASS ", StringComparison.Ordinal) ? "PASS ****"
                : command.Length > 200 ? $"{command[..60]}... ({command.Length:N0} characters)" : command;

        public async Task<Reply> CommandAsync(string command)
        {
            Console.WriteLine("[ftp] > " + Shown(command));
            await _writer.WriteLineAsync(command);
            var reply = await ReadReplyAsync();
            Console.WriteLine($"[ftp] < {reply.Code} {reply.Text}");
            return reply;
        }

        public async Task<List<Line>> ListAsync(string path)
        {
            using var data = await PassiveAsync();
            var reply = await CommandAsync("LIST " + path);
            Assert.IsTrue(reply.Code is 150 or 125, reply.Text);
            using var reader = new StreamReader(data.GetStream(), Encoding.UTF8);
            var text = await reader.ReadToEndAsync();
            data.Close();
            var done = await ReadReplyAsync();
            Assert.AreEqual(226, done.Code, done.Text);
            var lines = new List<Line>();
            foreach (var raw in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                // drwxr-xr-x 1 owner group size Mon DD HH:MM name
                var parts = raw.TrimEnd('\r').Split(' ', 9, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 9 || parts[8] is "." or "..")
                {
                    continue;
                }

                lines.Add(new Line(parts[8], parts[0][0] == 'd', long.Parse(parts[4]), parts[0]));
            }

            return lines;
        }

        public async Task<byte[]> RetrieveAsync(string path, long? restart = null)
        {
            using var data = await PassiveAsync();
            if (restart is { } offset)
            {
                var rest = await CommandAsync("REST " + offset);
                Assert.AreEqual(350, rest.Code, rest.Text);
            }

            var reply = await CommandAsync("RETR " + path);
            Assert.IsTrue(reply.Code is 150 or 125, reply.Text);
            using var buffer = new MemoryStream();
            await data.GetStream().CopyToAsync(buffer);
            data.Close();
            var done = await ReadReplyAsync();
            Assert.AreEqual(226, done.Code, done.Text);
            return buffer.ToArray();
        }

        /// <summary>RETR's first reply only (for files that should fail), with the data connection cleaned up.</summary>
        public async Task<Reply> RetrieveReplyAsync(string path)
        {
            using var data = await PassiveAsync();
            var reply = await CommandAsync("RETR " + path);
            if (reply.Code is 150 or 125)
            {
                await data.GetStream().CopyToAsync(Stream.Null);
                data.Close();
                await ReadReplyAsync();
            }

            return reply;
        }

        public Task<Reply> StoreAsync(string path, byte[] bytes) => UploadAsync(path, bytes, "STOR");

        public async Task<Reply> UploadAsync(string path, byte[] bytes, string command)
        {
            using var data = await PassiveAsync();
            var reply = await CommandAsync(command + " " + path);
            if (reply.Code is not (150 or 125))
            {
                return reply;
            }

            await data.GetStream().WriteAsync(bytes);
            data.Close();
            return await ReadReplyAsync();
        }

        /// <summary>MLSD's fact lines, as sent.</summary>
        public async Task<List<string>> MlsdAsync(string path)
        {
            using var data = await PassiveAsync();
            var reply = await CommandAsync("MLSD " + path);
            Assert.IsTrue(reply.Code is 150 or 125, reply.Text);
            using var reader = new StreamReader(data.GetStream(), Encoding.UTF8);
            var text = await reader.ReadToEndAsync();
            data.Close();
            Assert.AreEqual(226, (await ReadReplyAsync()).Code);
            return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        }

        /// <summary>NLST over a data connection the server makes to <paramref name="listener"/> (after a PORT).</summary>
        public async Task<List<string>> ListNamesActiveAsync(string path, TcpListener listener)
        {
            var reply = await CommandAsync("NLST " + path);
            Assert.IsTrue(reply.Code is 150 or 125, reply.Text);
            using var data = await listener.AcceptTcpClientAsync();
            using var reader = new StreamReader(data.GetStream(), Encoding.UTF8);
            var text = await reader.ReadToEndAsync();
            data.Close();
            Assert.AreEqual(226, (await ReadReplyAsync()).Code);
            return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        }

        public async Task<byte[]> RetrieveActiveAsync(string path, TcpListener listener)
        {
            var reply = await CommandAsync("RETR " + path);
            Assert.IsTrue(reply.Code is 150 or 125, reply.Text);
            using var data = await listener.AcceptTcpClientAsync();
            using var buffer = new MemoryStream();
            await data.GetStream().CopyToAsync(buffer);
            data.Close();
            Assert.AreEqual(226, (await ReadReplyAsync()).Code);
            return buffer.ToArray();
        }

        public Task SendAsync(string command)
        {
            Console.WriteLine("[ftp] > " + Shown(command));
            return _writer.WriteLineAsync(command);
        }

        /// <summary>This end of the control connection, as the server's log names the session.</summary>
        public IPEndPoint Local
        {
            get
            {
                var local = (IPEndPoint)_control.Client.LocalEndPoint!;
                return local.Address.IsIPv4MappedToIPv6 ? new IPEndPoint(local.Address.MapToIPv4(), local.Port) : local;
            }
        }

        /// <summary>Ends the control connection with a reset and no QUIT, as a client that died does.</summary>
        public void Reset()
        {
            _control.Client.LingerState = new LingerOption(true, 0);
            _control.Client.Close();
        }

        public async Task<long> SizeAsync(string path)
        {
            var reply = await CommandAsync("SIZE " + path);
            Assert.AreEqual(213, reply.Code, reply.Text);
            return long.Parse(reply.Text.Trim());
        }

        public async Task<TcpClient> PassiveAsync()
        {
            await CommandAsync("TYPE I");
            var reply = await CommandAsync("PASV");
            Assert.AreEqual(227, reply.Code, reply.Text);
            var open = reply.Text.IndexOf('(');
            var close = reply.Text.IndexOf(')');
            var numbers = reply.Text[(open + 1)..close].Split(',').Select(int.Parse).ToArray();
            var port = numbers[4] * 256 + numbers[5];
            var data = new TcpClient();
            await data.ConnectAsync(IPAddress.Loopback, port);
            return data;
        }

        public async Task<Reply> ReadReplyAsync(TimeSpan? patience = null)
        {
            using var timeout = new CancellationTokenSource(patience ?? TimeSpan.FromSeconds(20));
            var first = await _reader.ReadLineAsync(timeout.Token) ?? throw new IOException("the control connection closed");
            var code = int.Parse(first[..3]);
            var text = new StringBuilder(first[3..].TrimStart('-', ' '));
            if (first.Length > 3 && first[3] == '-')
            {
                string? line;
                while ((line = await _reader.ReadLineAsync(timeout.Token)) is not null)
                {
                    text.Append('\n').Append(line);
                    if (line.StartsWith(first[..3] + " ", StringComparison.Ordinal))
                    {
                        break;
                    }
                }
            }

            return new Reply(code, text.ToString());
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _writer.WriteLineAsync("QUIT");
            }
            catch (IOException)
            {
            }

            _control.Dispose();
        }
    }
}
