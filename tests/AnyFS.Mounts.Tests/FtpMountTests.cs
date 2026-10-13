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

    private async Task<(IMount Mount, TestMountContext Context, int Port)> StartAsync(ISource source, string collection = "work", int? port = null, TestMountContext? shareWith = null, bool readOnly = false, string? user = null, string? password = null, bool anonymous = true)
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

        public static async Task<FtpClient> ConnectAsync(int port)
        {
            var control = new TcpClient();
            await control.ConnectAsync(IPAddress.Loopback, port);
            var client = new FtpClient(control);
            var banner = await client.ReadReplyAsync();
            Assert.AreEqual(220, banner.Code, banner.Text);
            await client.CommandAsync("OPTS UTF8 ON");
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

        public async Task<Reply> StoreAsync(string path, byte[] bytes)
        {
            using var data = await PassiveAsync();
            var reply = await CommandAsync("STOR " + path);
            if (reply.Code is not (150 or 125))
            {
                return reply;
            }

            await data.GetStream().WriteAsync(bytes);
            data.Close();
            return await ReadReplyAsync();
        }

        public async Task<long> SizeAsync(string path)
        {
            var reply = await CommandAsync("SIZE " + path);
            Assert.AreEqual(213, reply.Code, reply.Text);
            return long.Parse(reply.Text.Trim());
        }

        private async Task<TcpClient> PassiveAsync()
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

        private async Task<Reply> ReadReplyAsync()
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
