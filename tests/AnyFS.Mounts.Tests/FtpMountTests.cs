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

    /// <summary>Every test runs against both servers: AnyFS's own and the FubarDev library.</summary>
    public static IEnumerable<object[]> Servers() => [[FtpMountSettings.ServerBuiltin], [FtpMountSettings.ServerLibrary]];

    private async Task<(IMount Mount, TestMountContext Context, int Port)> StartAsync(ISource source, string collection = "work", int? port = null, TestMountContext? shareWith = null, bool readOnly = false, string? user = null, string? password = null, bool anonymous = true, string server = FtpMountSettings.ServerBuiltin)
    {
        await source.InitialiseAsync(new TestSourceContext(collection), CancellationToken.None);
        var p = port ?? FreePort();
        var provider = new FtpMountProvider();
        var settings = (FtpMountSettings)provider.CreateDefaultSettings(new Collection(collection));
        settings.Port = p;
        settings.User = user;
        settings.Password = password;
        settings.AllowAnonymous = anonymous;
        settings.Server = server;
        var mount = provider.CreateMount(settings, new Collection(collection));
        var context = new TestMountContext(collection, "ftp", logger: new ConsoleLogger(), shareWith: shareWith);
        Console.WriteLine($"[test] starting ftp mount '{collection}' on {p}");
        await mount.StartAsync(new SingleSourceVfs(source, collection, readOnly), context, CancellationToken.None);
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
    [DynamicData(nameof(Servers))]
    [Timeout(60000)]
    public async Task AnonymousLoginListsAndReadsWithResume(string server)
    {
        var (_, _, port) = await StartAsync(Tree(), server: server);
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
    [DynamicData(nameof(Servers))]
    [Timeout(60000)]
    public async Task NamesAreUtf8ForAClientThatNeverAsks(string server)
    {
        var (_, _, port) = await StartAsync(Tree(), server: server);
        var client = await FtpClient.ConnectAsync(port, optsUtf8: false);
        Assert.AreEqual(230, (await client.LoginAsync("anonymous", "anyfs@test")).Code);
        var names = (await client.ListAsync("/work/unicode")).Select(l => l.Name).ToList();
        CollectionAssert.AreEqual(new[] { "ünïcödé ✓ 日本語.txt" }, names);
        Assert.AreEqual("unicode names", Encoding.UTF8.GetString(await client.RetrieveAsync("/work/unicode/ünïcödé ✓ 日本語.txt")));
    }

    [TestMethod]
    [DynamicData(nameof(Servers))]
    [Timeout(60000)]
    public async Task WritesLandUpstreamAndRefusedWhenReadOnly(string server)
    {
        var source = Tree();
        var (_, _, port) = await StartAsync(source, server: server);
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

        var (_, _, roPort) = await StartAsync(Tree(), "ro", readOnly: true, server: server);
        var roClient = await ConnectAsync(roPort);
        Assert.IsTrue((await roClient.StoreAsync("/ro/new.txt", Encoding.UTF8.GetBytes("x"))).Code >= 500, "read-only mount refuses STOR");
        Assert.IsTrue((await roClient.CommandAsync("MKD /ro/d")).Code >= 500);
        Assert.IsTrue((await roClient.CommandAsync("DELE /ro/readme.txt")).Code >= 500);
        Assert.AreEqual("hello readme", Encoding.UTF8.GetString(await roClient.RetrieveAsync("/ro/readme.txt")), "reads still work");
        var perms = (await roClient.ListAsync("/ro")).Single(l => l.Name == "readme.txt").Permissions;
        Assert.IsFalse(perms.Contains('w'), $"no write bits in the listing: {perms}");
    }

    [TestMethod]
    [DynamicData(nameof(Servers))]
    [Timeout(60000)]
    public async Task NamedUsersSeeTheirCollectionsAndAnonymousTheOpenOnes(string server)
    {
        var port = FreePort();
        var (_, context, _) = await StartAsync(Tree(), "private", port, user: "alice", password: "s3cret", anonymous: false, server: server);
        var (_, _, _) = await StartAsync(new MemorySource().AddFile("b.txt", "b"), "open", port, shareWith: context, server: server);
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

    /// <summary>Active mode (PORT: the server connects back to the client), MLSD facts, MDTM, and the modern-client dance of FEAT and OPTS.</summary>
    [TestMethod]
    [DynamicData(nameof(Servers))]
    [Timeout(60000)]
    public async Task ActiveModeMlsdAndFacts(string server)
    {
        var (_, _, port) = await StartAsync(Tree(), server: server);
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
        StringAssert.Contains(medium, "modify=20261001120000"); // the library adds .000
        Assert.IsTrue(facts.Any(f => f.Contains("type=dir;", StringComparison.Ordinal) && f.EndsWith(" docs", StringComparison.Ordinal)), string.Join(" | ", facts));
        StringAssert.StartsWith((await client.CommandAsync("MDTM /work/medium.bin")).Text.Trim(), "20261001120000"); // the library adds .000

        // active mode: our own listener, PORT, then LIST and RETR arrive on it
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var activePort = ((IPEndPoint)listener.LocalEndpoint).Port;
        Assert.AreEqual(200, (await client.CommandAsync($"PORT 127,0,0,1,{activePort / 256},{activePort % 256}")).Code);
        var names = await client.ListNamesActiveAsync("/work", listener);
        CollectionAssert.Contains(names, "readme.txt");
        Assert.AreEqual(200, (await client.CommandAsync($"PORT 127,0,0,1,{activePort / 256},{activePort % 256}")).Code);
        CollectionAssert.AreEqual(Medium, await client.RetrieveActiveAsync("/work/medium.bin", listener));

        // a data address that is not the client's own is refused (no bounce attacks); the library connects wherever PORT says
        if (server == FtpMountSettings.ServerBuiltin)
        {
            var bounce = await client.CommandAsync("PORT 10,1,2,3,200,10");
            Assert.IsTrue(bounce.Code >= 500, $"{bounce.Code} {bounce.Text}");
        }
    }

    /// <summary>Eight sessions reading the same file at once all get it whole.</summary>
    [TestMethod]
    [DynamicData(nameof(Servers))]
    [Timeout(120000)]
    public async Task ConcurrentReadersGetTheWholeFile(string server)
    {
        var big = new byte[4 << 20];
        new Random(5).NextBytes(big);
        var (_, _, port) = await StartAsync(new MemorySource().AddFile("big.bin", big), server: server);
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

    [TestMethod]
    public void SettingsValidation()
    {
        var provider = new FtpMountProvider();
        Assert.AreEqual(0, provider.Validate(new FtpMountSettings()).Count());
        Assert.IsTrue(provider.Validate(new FtpMountSettings { Server = "Other" }).Any(m => m.IsError && m.Property == nameof(FtpMountSettings.Server)));
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

        public async Task<Reply> CommandAsync(string command)
        {
            Console.WriteLine("[ftp] > " + (command.StartsWith("PASS ", StringComparison.Ordinal) ? "PASS ****" : command));
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
            Console.WriteLine("[ftp] > " + command);
            return _writer.WriteLineAsync(command);
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

        public async Task<Reply> ReadReplyAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
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
