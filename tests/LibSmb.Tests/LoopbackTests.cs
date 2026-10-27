using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using LibSmb.Protocol;
using static LibSmb.Tests.TestSupport;

namespace LibSmb.Tests;

/// <summary>
/// The server over a real TCP connection, with the tests' own SMB2 client: portable-smb-server's loopback tests
/// (test/e2e_test.go, stress_e2e_test.go, internal/smb/server_test.go) ported, and what only the wire shows (signed
/// sessions on every dialect, compound chains, the SMB1 upgrade, shares coming and going).
/// </summary>
[TestClass]
public class LoopbackTests
{
    private const string User = "user";
    private const string Password = "password";
    private readonly List<string> _dirs = [];
    private readonly List<SmbServer> _servers = [];

    [TestCleanup]
    public async Task Cleanup()
    {
        foreach (var server in _servers)
        {
            await server.DisposeAsync();
        }

        foreach (var dir in _dirs)
        {
            Remove(dir);
        }
    }

    private string Dir()
    {
        var dir = TempDir();
        _dirs.Add(dir);
        return dir;
    }

    private IPEndPoint Serve(string? user, string? password, params (string Name, string Dir)[] shares)
    {
        var server = SmbServer.Start(
            new SmbServerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, 0), User = user, Password = password },
            shares.Select(s => new SmbShare(s.Name, new LocalFileSystem(s.Dir))));
        _servers.Add(server);
        return server.LocalEndPoint;
    }

    private IPEndPoint Serve(string dir) => Serve(User, Password, ("data", dir));

    /// <summary>Write, read back, folders, rename and delete with an account, the bytes checked on disk.</summary>
    [TestMethod]
    public async Task FilesRoundTrip()
    {
        var dir = Dir();
        await using var client = await SmbTestClient.SignInAsync(Serve(dir), User, Password);
        Assert.IsFalse(client.Guest);
        var tree = await client.TreeConnectAsync("data");

        await WriteFileAsync(client, tree, "new.txt", "written data"u8.ToArray());
        Assert.AreEqual("written data", Encoding.UTF8.GetString(await ReadFileAsync(client, tree, "new.txt")));
        Assert.AreEqual("written data", await File.ReadAllTextAsync(Path.Combine(dir, "new.txt")));

        await MakeDirectoryAsync(client, tree, "sub");
        await WriteFileAsync(client, tree, @"sub\inner.txt", "inner"u8.ToArray());
        await RenameAsync(client, tree, "new.txt", "renamed.txt");
        Assert.AreEqual(Status.ObjectNameNotFound, (await client.CreateAsync(tree, "new.txt")).Status);
        await DeleteAsync(client, tree, "renamed.txt");
        await DeleteAsync(client, tree, @"sub\inner.txt");
        await DeleteAsync(client, tree, "sub");
        Assert.IsEmpty(await client.ListAsync(tree, ""));
        Assert.IsEmpty(Directory.EnumerateFileSystemEntries(dir));
    }

    /// <summary>The account signs in; a wrong password and a wrong user do not.</summary>
    [TestMethod]
    public async Task OnlyTheAccountSignsIn()
    {
        var dir = Dir();
        await File.WriteAllTextAsync(Path.Combine(dir, "secret.txt"), "top secret");
        var server = Serve("alice", "s3cret", ("data", dir));

        await using (var client = await SmbTestClient.SignInAsync(server, "alice", "s3cret"))
        {
            var tree = await client.TreeConnectAsync("data");
            Assert.AreEqual("top secret", Encoding.UTF8.GetString(await ReadFileAsync(client, tree, "secret.txt")));
        }

        await using (var client = await SmbTestClient.SignInAsync(server, "ALICE", "s3cret"))
        {
            Assert.IsFalse(client.Guest, "the user name in another case");
        }

        foreach (var (user, password) in new[] { ("alice", "wrong"), ("bob", "s3cret"), ((string?)null, (string?)null) })
        {
            await using var client = await SmbTestClient.ConnectAsync(server);
            await client.NegotiateAsync([Dialect.Smb302]);
            Assert.AreEqual(Status.LogonFailure, await client.SessionSetupAsync(user, password), $"{user ?? "anonymous"} / {password}");

            // and nothing works without a session that signed in
            Assert.AreEqual(Status.UserSessionDeleted, (await client.SendAsync(Command.TreeConnect, new byte[8])).Status);
        }
    }

    /// <summary>A guest server lets anyone in as a guest, without signing.</summary>
    [TestMethod]
    public async Task AGuestServerLetsAnyoneIn()
    {
        var dir = Dir();
        await File.WriteAllTextAsync(Path.Combine(dir, "f.txt"), "guest ok");
        var server = Serve(null, null, ("data", dir));
        foreach (var user in new[] { "anyone", null })
        {
            await using var client = await SmbTestClient.SignInAsync(server, user, "");
            Assert.IsTrue(client.Guest);
            var tree = await client.TreeConnectAsync("data");
            var response = await client.CreateAsync(tree, "f.txt");
            Assert.IsFalse(response.Signed, "a guest session is not signed");
            Assert.AreEqual("guest ok", Encoding.UTF8.GetString(await ReadFileAsync(client, tree, "f.txt")));
        }
    }

    /// <summary>Two shares are both served and see only their own files.</summary>
    [TestMethod]
    public async Task SharesAreKeptApart()
    {
        var dir1 = Dir();
        var dir2 = Dir();
        await File.WriteAllTextAsync(Path.Combine(dir1, "one.txt"), "1");
        await File.WriteAllTextAsync(Path.Combine(dir2, "two.txt"), "2");
        await using var client = await SmbTestClient.SignInAsync(Serve(User, Password, ("first", dir1), ("second", dir2)), User, Password);
        var first = await client.TreeConnectAsync("first");
        var second = await client.TreeConnectAsync("second");
        Assert.AreEqual(Status.Success, (await client.CreateAsync(first, "one.txt")).Status);
        Assert.AreEqual(Status.ObjectNameNotFound, (await client.CreateAsync(first, "two.txt")).Status);
        Assert.AreEqual(Status.Success, (await client.CreateAsync(second, "two.txt")).Status);
        Assert.AreEqual(Status.BadNetworkName, (await client.SendAsync(Command.TreeConnect, TreeConnectBody("third"))).Status);
    }

    /// <summary>
    /// Every dialect signs: HMAC-SHA256 on 2.0.2 and 2.1, AES-CMAC on 3.0 and 3.0.2. Each response is signed and checked;
    /// a request whose signature is wrong is refused.
    /// </summary>
    [TestMethod]
    public async Task EveryDialectSigns()
    {
        var dir = Dir();
        await File.WriteAllTextAsync(Path.Combine(dir, "f.txt"), "signed");
        var server = Serve(dir);
        foreach (var dialect in new[] { Dialect.Smb202, Dialect.Smb210, Dialect.Smb300, Dialect.Smb302 })
        {
            await using var client = await SmbTestClient.SignInAsync(server, User, Password, [dialect]);
            Assert.AreEqual(dialect, client.Dialect);
            var tree = await client.TreeConnectAsync("data");
            var open = await client.CreateAsync(tree, "f.txt");
            Assert.IsTrue(open.Signed, $"0x{dialect:X4}: the response is signed");
            Assert.AreEqual("signed", Encoding.UTF8.GetString(await ReadFileAsync(client, tree, "f.txt")));

            // unsigned requests pass (the server does not require signing); a wrong signature does not
            client.SignRequests = false;
            Assert.AreEqual(Status.Success, (await client.EchoAsync()).Status);
            var echo = new byte[Smb2.HeaderSize + 4];
            Smb2.Magic.CopyTo(echo);
            Wire.Put16(echo, 4, Smb2.HeaderSize);
            Wire.Put16(echo, 12, (ushort)Command.Echo);
            Wire.Put32(echo, 16, (uint)HeaderFlags.Signed);
            Wire.Put64(echo, 24, 1000);
            Wire.Put64(echo, 40, client.SessionId);
            Wire.Put16(echo, Smb2.HeaderSize, 4);
            var reply = SmbTestClient.Parse(await client.RoundTripAsync(echo));
            Assert.AreEqual(Status.AccessDenied, reply.Status, "a request signed wrongly");
        }
    }

    /// <summary>Linux's cifs sends NTLMSSP bare, not in SPNEGO: answered bare, and signed in.</summary>
    [TestMethod]
    public async Task BareNtlmSignsIn()
    {
        var dir = Dir();
        await File.WriteAllTextAsync(Path.Combine(dir, "f.txt"), "bare");
        await using var client = await SmbTestClient.SignInAsync(Serve(dir), User, Password, [Dialect.Smb300], spnego: false);
        var tree = await client.TreeConnectAsync("data");
        Assert.AreEqual("bare", Encoding.UTF8.GetString(await ReadFileAsync(client, tree, "f.txt")));
    }

    /// <summary>10 MiB of random bytes across many READs and WRITEs, intact on both ends.</summary>
    [TestMethod]
    public async Task ALargeFileRoundTrips()
    {
        var dir = Dir();
        await using var client = await SmbTestClient.SignInAsync(Serve(dir), User, Password);
        var tree = await client.TreeConnectAsync("data");
        var payload = RandomNumberGenerator.GetBytes(10 << 20);
        await WriteFileAsync(client, tree, "big.bin", payload, chunk: 1 << 20);
        CollectionAssert.AreEqual(payload, await ReadFileAsync(client, tree, "big.bin", chunk: 1 << 20));
        CollectionAssert.AreEqual(payload, await File.ReadAllBytesAsync(Path.Combine(dir, "big.bin")));
    }

    /// <summary>A folder too big for one QUERY_DIRECTORY reply, every entry listed once.</summary>
    [TestMethod]
    public async Task AFolderTooBigForOneReplyIsListedWhole()
    {
        var dir = Dir();
        for (var i = 0; i < 500; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(dir, $"file-{i:D3}-with-a-reasonably-long-name.txt"), "x");
        }

        await using var client = await SmbTestClient.SignInAsync(Serve(dir), User, Password);
        var tree = await client.TreeConnectAsync("data");
        foreach (var infoClass in new byte[] { 0x01, 0x02, 0x03, 0x0C, 0x25, 0x26 })
        {
            var names = await client.ListAsync(tree, "", infoClass: infoClass);
            Assert.HasCount(500, names, $"class 0x{infoClass:X2}");
            Assert.HasCount(500, names.Distinct());
        }
    }

    [TestMethod]
    public async Task AnEmptyFileRoundTrips()
    {
        await using var client = await SmbTestClient.SignInAsync(Serve(Dir()), User, Password);
        var tree = await client.TreeConnectAsync("data");
        await WriteFileAsync(client, tree, "empty.txt", []);
        Assert.IsEmpty(await ReadFileAsync(client, tree, "empty.txt"));
    }

    /// <summary>Non-ASCII and awkward (legal) names, written, read and listed.</summary>
    [TestMethod]
    public async Task SpecialNames()
    {
        await using var client = await SmbTestClient.SignInAsync(Serve(Dir()), User, Password);
        var tree = await client.TreeConnectAsync("data");
        var names = new[] { "ünïcödé.txt", "日本語ファイル.txt", "name with spaces.txt", "dots.in.name.txt", "emoji-😀.txt", ".leading-dot", new string('n', 200) + ".txt" };
        foreach (var name in names)
        {
            await WriteFileAsync(client, tree, name, Encoding.UTF8.GetBytes(name));
            Assert.AreEqual(name, Encoding.UTF8.GetString(await ReadFileAsync(client, tree, name)));
        }

        CollectionAssert.AreEquivalent(names, await client.ListAsync(tree, ""));
    }

    /// <summary>
    /// A 5,000-entry folder listed in one enumeration, then 300 of its names looked up one by one (as a macOS client
    /// without Apple's extensions does for every entry): both in seconds.
    /// </summary>
    [TestMethod]
    public async Task AFolderOf5000ListsAndLooksUpQuickly()
    {
        var dir = Dir();
        var many = Path.Combine(dir, "many");
        Directory.CreateDirectory(many);
        for (var i = 0; i < 5000; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(many, $"file-{i:D5}.txt"), "x");
        }

        await using var client = await SmbTestClient.SignInAsync(Serve(dir), User, Password);
        var tree = await client.TreeConnectAsync("data");
        var timer = Stopwatch.StartNew();
        Assert.HasCount(5000, await client.ListAsync(tree, "many"));
        Assert.IsLessThan(20, timer.Elapsed.TotalSeconds, "the listing");

        timer.Restart();
        var directory = await client.OpenAsync(tree, "many", options: CreateOptions.DirectoryFile);
        for (var i = 0; i < 300; i++)
        {
            var name = $"file-{i * 17 % 5000:D5}.txt";
            var response = await client.SendAsync(Command.QueryDirectory, SmbTestClient.QueryDirectoryBody(directory, name, 0x25, QueryDirectoryFlags.RestartScans | QueryDirectoryFlags.ReturnSingleEntry), tree);
            Assert.AreEqual(Status.Success, response.Status, name);
            CollectionAssert.AreEqual(new[] { name }, SmbTestClient.DirectoryNames(response, 0x25));
        }

        Assert.IsLessThan(30, timer.Elapsed.TotalSeconds, "300 lookups by name");
    }

    /// <summary>Eight sessions read the same 8 MiB file at once and every copy is right.</summary>
    [TestMethod]
    public async Task EightReadersAtOnce()
    {
        var dir = Dir();
        var data = new byte[8 << 20];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i * 7 + (i >> 12));
        }

        await File.WriteAllBytesAsync(Path.Combine(dir, "big.bin"), data);
        var server = Serve(dir);
        var expected = Convert.ToHexString(SHA256.HashData(data));
        var readers = Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var client = await SmbTestClient.SignInAsync(server, User, Password);
            var tree = await client.TreeConnectAsync("data");
            return Convert.ToHexString(SHA256.HashData(await ReadFileAsync(client, tree, "big.bin", chunk: 1 << 20)));
        });
        foreach (var hash in await Task.WhenAll(readers))
        {
            Assert.AreEqual(expected, hash);
        }
    }

    /// <summary>
    /// A burst of requests in one write, as a client with credits sends them, answered one reply per request, in order:
    /// the writer that lets the next request be taken up while the last reply goes out never reorders or drops one.
    /// </summary>
    [TestMethod]
    public async Task PipelinedRequestsAreAnsweredInOrder()
    {
        var endpoint = Serve(Dir());
        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync(endpoint.Address, endpoint.Port);
        var stream = tcp.GetStream();
        const int count = 24; // more than the writer's queue holds
        var burst = new List<byte>();
        for (var i = 0; i < count; i++)
        {
            var request = new byte[Smb2.HeaderSize + 4];
            Smb2.Magic.CopyTo(request);
            Wire.Put16(request, 4, Smb2.HeaderSize);
            Wire.Put16(request, 12, (ushort)Command.Negotiate);
            Wire.Put64(request, 24, (ulong)i);
            burst.AddRange([0, 0, 0, (byte)request.Length]);
            burst.AddRange(request);
        }

        await stream.WriteAsync(burst.ToArray());
        for (var i = 0; i < count; i++)
        {
            var header = new byte[4];
            await stream.ReadExactlyAsync(header);
            var reply = new byte[(header[1] << 16) | (header[2] << 8) | header[3]];
            await stream.ReadExactlyAsync(reply);
            Assert.AreEqual((ulong)i, Wire.U64(reply, 24), $"reply {i}");
        }
    }

    /// <summary>A legacy SMB1 negotiate gets the SMB2 wildcard dialect, which sends the client on to SMB2.</summary>
    [TestMethod]
    public async Task AnSmb1NegotiateIsMovedOnToSmb2()
    {
        await using var client = await SmbTestClient.ConnectAsync(Serve(Dir()));
        byte[] smb1 = [0xFF, (byte)'S', (byte)'M', (byte)'B', 0x72, .. new byte[27], .. "\x02SMB 2.002\0\x02SMB 2.???\0"u8];
        var reply = SmbTestClient.Parse(await client.RoundTripAsync(smb1));
        Assert.AreEqual(Command.Negotiate, reply.Command);
        Assert.AreEqual(Status.Success, reply.Status);
        Assert.AreEqual(Dialect.Wildcard, Wire.U16(reply.Body, 4));
        Assert.AreEqual(Status.Success, (await client.NegotiateAsync([Dialect.Smb210])).Status);
    }

    /// <summary>
    /// A compound CREATE, QUERY_INFO, CLOSE with the related ones' FileId as the placeholder (how Linux and macOS stat a
    /// path), signed: one compound reply, each part signed, the size right; and of a missing name, all three fail alike.
    /// </summary>
    [TestMethod]
    public async Task ARelatedCompoundChainIsAnsweredAsOne()
    {
        var dir = Dir();
        await File.WriteAllTextAsync(Path.Combine(dir, "f.txt"), "hello world");
        await using var client = await SmbTestClient.SignInAsync(Serve(dir), User, Password, [Dialect.Smb302]);
        var tree = await client.TreeConnectAsync("data");
        var responses = await client.SendCompoundAsync(
            [
                (Command.Create, SmbTestClient.CreateBody("f.txt")),
                (Command.QueryInfo, SmbTestClient.QueryInfoBody(SmbTestClient.NoFileId, InfoType.File, FileInfoClass.NetworkOpen)),
                (Command.Close, SmbTestClient.CloseBody(SmbTestClient.NoFileId)),
            ],
            tree,
            related: true);
        Assert.HasCount(3, responses);
        Assert.IsTrue(responses.All(r => r.Status == Status.Success && r.Signed), string.Join(", ", responses.Select(r => $"{r.Command} {r.Status:X8} {r.Signed}")));
        Assert.AreEqual(11ul, Wire.U64(responses[1].Body, 8 + 40), "EndOfFile");

        responses = await client.SendCompoundAsync(
            [
                (Command.Create, SmbTestClient.CreateBody("missing.txt")),
                (Command.QueryInfo, SmbTestClient.QueryInfoBody(SmbTestClient.NoFileId, InfoType.File, FileInfoClass.NetworkOpen)),
                (Command.Close, SmbTestClient.CloseBody(SmbTestClient.NoFileId)),
            ],
            tree,
            related: true);
        Assert.IsTrue(responses.All(r => r.Status == Status.ObjectNameNotFound), string.Join(", ", responses.Select(r => $"{r.Command} {r.Status:X8}")));
    }

    /// <summary>LOGOFF ends the session: the next command on it is refused.</summary>
    [TestMethod]
    public async Task LogoffEndsTheSession()
    {
        await using var client = await SmbTestClient.SignInAsync(Serve(Dir()), User, Password);
        Assert.AreEqual(Status.Success, (await client.EchoAsync()).Status);
        var logoff = await client.LogoffAsync();
        Assert.AreEqual(Status.Success, logoff.Status);
        Assert.IsTrue(logoff.Signed, "signed with the key of the session it ends");
        client.SignRequests = false;
        Assert.AreEqual(Status.UserSessionDeleted, (await client.EchoAsync()).Status);
    }

    /// <summary>
    /// Shares come and go while the server runs and a client is connected: a new one can be connected to at once, a
    /// removed one answers NETWORK_NAME_DELETED on its tree.
    /// </summary>
    [TestMethod]
    public async Task SharesComeAndGoWhileServing()
    {
        var dir1 = Dir();
        var dir2 = Dir();
        await File.WriteAllTextAsync(Path.Combine(dir2, "late.txt"), "late");
        var server = SmbServer.Start(new SmbServerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, 0), User = User, Password = Password }, [new SmbShare("first", new LocalFileSystem(dir1))]);
        _servers.Add(server);
        await using var client = await SmbTestClient.SignInAsync(server.LocalEndPoint, User, Password);
        var first = await client.TreeConnectAsync("first");

        server.AddShare(new SmbShare("late", new LocalFileSystem(dir2)));
        var late = await client.TreeConnectAsync("late");
        Assert.AreEqual("late", Encoding.UTF8.GetString(await ReadFileAsync(client, late, "late.txt")));

        var open = await client.OpenAsync(late, "late.txt");
        Assert.IsTrue(await server.RemoveShareAsync("late"));
        Assert.AreEqual(Status.NetworkNameDeleted, (await client.CreateAsync(late, "late.txt")).Status);
        Assert.AreEqual(Status.InvalidParameter, (await client.ReadRawAsync(late, open, 0, 4)).Status, "its opens are closed");
        Assert.AreEqual(Status.Success, (await client.CreateAsync(first, "")).Status, "the other share is untouched");
    }

    /// <summary>Disposing the server ends a live connection promptly; disposing it again does nothing.</summary>
    [TestMethod]
    public async Task DisposingTheServerEndsConnections()
    {
        var server = SmbServer.Start(new SmbServerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, 0) }, [new SmbShare("data", new LocalFileSystem(Dir()))]);
        await using var client = await SmbTestClient.SignInAsync(server.LocalEndPoint, null, null);
        var disposing = server.DisposeAsync().AsTask();
        Assert.AreSame(disposing, await Task.WhenAny(disposing, Task.Delay(TimeSpan.FromSeconds(5))), "disposed within 5 s with a live connection");
        await server.DisposeAsync();
        await Assert.ThrowsAsync<Exception>(async () =>
        {
            await client.EchoAsync();
            await client.EchoAsync();
        });
    }

    /// <summary>Times set through the share, read back on both ends; the free space is the volume's.</summary>
    [TestMethod]
    public async Task TimesAndSpace()
    {
        var dir = Dir();
        await File.WriteAllTextAsync(Path.Combine(dir, "f.txt"), "x");
        await using var client = await SmbTestClient.SignInAsync(Serve(dir), User, Password);
        var tree = await client.TreeConnectAsync("data");
        var when = new DateTimeOffset(2021, 6, 7, 8, 9, 10, TimeSpan.Zero);
        var fileId = await client.OpenAsync(tree, "f.txt", write: true);
        Assert.AreEqual(Status.Success, (await client.SetModifiedAsync(tree, fileId, when)).Status);
        var basic = await client.QueryInfoAsync(tree, fileId, InfoType.File, FileInfoClass.Basic);
        Assert.AreEqual(Wire.FileTime(when), Wire.U64(basic.Body, 8 + 16), "LastWriteTime");
        await client.CloseAsync(tree, fileId);
        Assert.AreEqual(when, new DateTimeOffset(File.GetLastWriteTimeUtc(Path.Combine(dir, "f.txt"))));

        var root = await client.OpenAsync(tree, "", options: CreateOptions.DirectoryFile);
        var size = await client.QueryInfoAsync(tree, root, InfoType.FileSystem, FsInfoClass.FullSize);
        Assert.AreEqual(Status.Success, size.Status);
        var unit = (long)Wire.U32(size.Body, 8 + 24) * Wire.U32(size.Body, 8 + 28);
        var drive = new DriveInfo(dir);
        Assert.AreEqual(drive.TotalSize / unit, (long)Wire.U64(size.Body, 8));
    }

    private static byte[] TreeConnectBody(string share)
    {
        var path = Encoding.Unicode.GetBytes(@"\\server\" + share);
        var body = new byte[8 + path.Length];
        Wire.Put16(body, 0, 9);
        Wire.Put16(body, 4, Smb2.HeaderSize + 8);
        Wire.Put16(body, 6, (ushort)path.Length);
        path.CopyTo(body, 8);
        return body;
    }

    private static async Task WriteFileAsync(SmbTestClient client, uint tree, string name, byte[] data, int chunk = 65536)
    {
        var fileId = await client.OpenAsync(tree, name, write: true, disposition: Disposition.OverwriteIf);
        for (var offset = 0; offset < data.Length; offset += chunk)
        {
            (await client.WriteAsync(tree, fileId, offset, data.AsMemory(offset, Math.Min(chunk, data.Length - offset)))).EnsureSuccess();
        }

        (await client.CloseAsync(tree, fileId)).EnsureSuccess();
    }

    private static async Task<byte[]> ReadFileAsync(SmbTestClient client, uint tree, string name, int chunk = 65536)
    {
        var fileId = await client.OpenAsync(tree, name);
        var data = new List<byte>();
        while (true)
        {
            var piece = await client.ReadAsync(tree, fileId, data.Count, chunk);
            if (piece.Length == 0)
            {
                break;
            }

            data.AddRange(piece);
        }

        (await client.CloseAsync(tree, fileId)).EnsureSuccess();
        return [.. data];
    }

    private static async Task MakeDirectoryAsync(SmbTestClient client, uint tree, string name)
    {
        var fileId = SmbTestClient.FileIdOf((await client.CreateAsync(tree, name, 0x00100081, Disposition.Create, CreateOptions.DirectoryFile)).EnsureSuccess());
        await client.CloseAsync(tree, fileId);
    }

    private static async Task RenameAsync(SmbTestClient client, uint tree, string from, string to)
    {
        var fileId = SmbTestClient.FileIdOf((await client.CreateAsync(tree, from, Access.Delete | 0x00100080)).EnsureSuccess());
        (await client.RenameAsync(tree, fileId, to)).EnsureSuccess();
        await client.CloseAsync(tree, fileId);
    }

    private static async Task DeleteAsync(SmbTestClient client, uint tree, string name)
    {
        var fileId = SmbTestClient.FileIdOf((await client.CreateAsync(tree, name, Access.Delete | 0x00100080)).EnsureSuccess());
        (await client.DeleteOnCloseAsync(tree, fileId)).EnsureSuccess();
        (await client.CloseAsync(tree, fileId)).EnsureSuccess();
    }
}
