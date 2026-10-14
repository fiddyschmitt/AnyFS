using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AnyFS.Mounts.Smb;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnyFS.Mounts.Tests;

/// <summary>
/// Spec 8.2 and 16.1: the VFS endpoint against the portable-smb-server contract (with the token and per-file readOnly),
/// and the real server started over it when its binary is around (tools/go-servers/&lt;rid&gt;/ or ANYFS_SMB_SERVER).
/// </summary>
[TestClass]
public class SmbMountTests
{
    private readonly List<IAsyncDisposable> _cleanup = [];

    [TestCleanup]
    public async Task Cleanup()
    {
        foreach (var d in Enumerable.Reverse(_cleanup))
        {
            await d.DisposeAsync();
        }
    }

    private static MemorySource Tree() => new MemorySource()
        .AddFile("readme.txt", "hello readme")
        .AddFile("docs/a.txt", "aaa")
        .AddFile("medium.bin", Enumerable.Range(0, 50_000).Select(i => (byte)(i * 31 % 253)).ToArray(), new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero))
        .AddDirectory("emptydir");

    private sealed record Collection(string Name) : ICollectionInfo
    {
        public Guid Id { get; } = Guid.NewGuid();
    }

    private async Task<(VfsEndpoint Endpoint, HttpClient Client, MemorySource Source)> EndpointAsync(bool readOnly = false, bool sourceCanWrite = true, string collection = "work")
    {
        var source = new MemorySource(canWrite: sourceCanWrite)
            .AddFile("readme.txt", "hello readme")
            .AddFile("docs/a.txt", "aaa")
            .AddDirectory("emptydir");
        await source.InitialiseAsync(new TestSourceContext(collection), CancellationToken.None);
        var endpoint = await VfsEndpoint.StartAsync(NullLogger.Instance, CancellationToken.None);
        _cleanup.Add(endpoint);
        var context = new TestMountContext(collection, "smb");
        endpoint.Register(collection, new SingleSourceVfs(source, collection, readOnly), context.Stats);
        var client = new HttpClient { BaseAddress = new Uri(endpoint.BaseUrlFor(collection) + "/") };
        client.DefaultRequestHeaders.Add(VfsEndpoint.TokenHeader, endpoint.Token);
        _cleanup.Add(new ClientDisposer(client));
        return (endpoint, client, source);
    }

    [TestMethod]
    public async Task EndpointRequiresTheToken()
    {
        var (endpoint, client, _) = await EndpointAsync();
        using var bare = new HttpClient { BaseAddress = client.BaseAddress };
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await bare.GetAsync("capabilities")).StatusCode, "no token");
        bare.DefaultRequestHeaders.Add(VfsEndpoint.TokenHeader, "wrong");
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await bare.GetAsync("stat?path=")).StatusCode, "wrong token");
        Assert.AreEqual(HttpStatusCode.OK, (await client.GetAsync("capabilities")).StatusCode);
        Assert.AreEqual(48, endpoint.Token.Length, "a per-launch random token");
    }

    [TestMethod]
    public async Task EndpointSpeaksTheContract()
    {
        var (_, client, source) = await EndpointAsync();
        using var caps = JsonDocument.Parse(await client.GetStringAsync("capabilities"));
        Assert.AreEqual("work", caps.RootElement.GetProperty("name").GetString());
        Assert.IsFalse(caps.RootElement.GetProperty("readOnly").GetBoolean());

        using var root = JsonDocument.Parse(await client.GetStringAsync("stat?path="));
        Assert.IsTrue(root.RootElement.GetProperty("isDir").GetBoolean());
        using var readme = JsonDocument.Parse(await client.GetStringAsync("stat?path=readme.txt"));
        Assert.AreEqual("readme.txt", readme.RootElement.GetProperty("name").GetString());
        Assert.AreEqual(12, readme.RootElement.GetProperty("size").GetInt64());
        Assert.IsFalse(readme.RootElement.GetProperty("isDir").GetBoolean());
        Assert.IsFalse(readme.RootElement.GetProperty("readOnly").GetBoolean());
        Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync("stat?path=nope")).StatusCode);

        using var list = JsonDocument.Parse(await client.GetStringAsync("list?path="));
        var names = list.RootElement.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToList();
        CollectionAssert.AreEquivalent(new[] { "readme.txt", "docs", "emptydir" }, names);

        Assert.AreEqual("lo re", Encoding.UTF8.GetString(await client.GetByteArrayAsync("read?path=readme.txt&offset=3&length=5")));
        Assert.AreEqual(0, (await client.GetByteArrayAsync("read?path=readme.txt&offset=100&length=5")).Length, "past the end: nothing");
        Assert.AreEqual(HttpStatusCode.BadRequest, (await client.GetAsync("read?path=readme.txt&offset=-1&length=5")).StatusCode);

        Assert.AreEqual(HttpStatusCode.OK, (await client.PostAsync("create?path=new.txt&exclusive=true", null)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Conflict, (await client.PostAsync("create?path=new.txt&exclusive=true", null)).StatusCode, "exclusive on an existing file");
        var written = await client.PutAsync("write?path=new.txt&offset=0", new ByteArrayContent("hello"u8.ToArray()));
        Assert.AreEqual(HttpStatusCode.OK, written.StatusCode);
        Assert.AreEqual(5, (await written.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("written").GetInt32());
        Assert.AreEqual("hello", Encoding.UTF8.GetString(source.GetFile("new.txt")!));
        Assert.AreEqual(HttpStatusCode.OK, (await client.PostAsync("truncate?path=new.txt&size=2", null)).StatusCode);
        Assert.AreEqual("he", Encoding.UTF8.GetString(source.GetFile("new.txt")!));
        Assert.AreEqual(HttpStatusCode.OK, (await client.PostAsync("chtimes?path=new.txt&modTime=2024-01-02T03:04:05Z", null)).StatusCode);
        using var stat = JsonDocument.Parse(await client.GetStringAsync("stat?path=new.txt"));
        Assert.AreEqual(new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero), stat.RootElement.GetProperty("modTime").GetDateTimeOffset());

        Assert.AreEqual(HttpStatusCode.OK, (await client.PostAsync("mkdir?path=made", null)).StatusCode);
        Assert.AreEqual(HttpStatusCode.Conflict, (await client.PostAsync("mkdir?path=made", null)).StatusCode);
        var rename = await client.PostAsJsonAsync("rename", new { from = "new.txt", to = "made/moved.txt", replace = false });
        Assert.AreEqual(HttpStatusCode.OK, rename.StatusCode, await rename.Content.ReadAsStringAsync());
        Assert.IsTrue(source.Exists("made/moved.txt"));
        Assert.AreEqual(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("rename", new { from = "readme.txt", to = "made/moved.txt", replace = false })).StatusCode, "target exists, no replace");
        Assert.AreEqual(HttpStatusCode.Conflict, (await client.DeleteAsync("remove?path=made")).StatusCode, "not empty");
        Assert.AreEqual(HttpStatusCode.OK, (await client.DeleteAsync("remove?path=made/moved.txt")).StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, (await client.DeleteAsync("remove?path=made")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await client.DeleteAsync("remove?path=made")).StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync("statfs")).StatusCode, "a memory source reports no volume: the server uses a synthetic one");
        Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync("nosuch?path=")).StatusCode);
    }

    [TestMethod]
    public async Task EndpointReportsReadOnlyThroughCapabilitiesAndPerFile()
    {
        var (_, roClient, _) = await EndpointAsync(readOnly: true);
        using var caps = JsonDocument.Parse(await roClient.GetStringAsync("capabilities"));
        Assert.IsTrue(caps.RootElement.GetProperty("readOnly").GetBoolean(), "a read-only mount: the server never calls a write endpoint");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await roClient.PostAsync("create?path=x.txt", null)).StatusCode, "and if it did");

        var (_, perFile, _) = await EndpointAsync(sourceCanWrite: false, collection: "ro");
        using var caps2 = JsonDocument.Parse(await perFile.GetStringAsync("capabilities"));
        Assert.IsFalse(caps2.RootElement.GetProperty("readOnly").GetBoolean(), "the mount is writable");
        using var readme = JsonDocument.Parse(await perFile.GetStringAsync("stat?path=readme.txt"));
        Assert.IsTrue(readme.RootElement.GetProperty("readOnly").GetBoolean(), "but the source can't write: the file is read-only (FILE_ATTRIBUTE_READONLY on the share)");
        using var list = JsonDocument.Parse(await perFile.GetStringAsync("list?path="));
        Assert.IsTrue(list.RootElement.GetProperty("entries").EnumerateArray().Single(e => e.GetProperty("name").GetString() == "readme.txt").GetProperty("readOnly").GetBoolean());
        Assert.AreEqual(HttpStatusCode.Forbidden, (await perFile.PutAsync("write?path=readme.txt&offset=0", new ByteArrayContent("x"u8.ToArray()))).StatusCode);
    }

    [TestMethod]
    public async Task TheRealServerStartsOverTheEndpointAndNegotiates()
    {
        var binary = FindServerBinary();
        if (binary is null)
        {
            Assert.Inconclusive("portable-smb-server binary not found: run scripts/fetch-go-servers.ps1 or set ANYFS_SMB_SERVER");
        }

        Environment.SetEnvironmentVariable(SmbServerBinary.EnvironmentVariable, binary);
        try
        {
            var source = Tree();
            await source.InitialiseAsync(new TestSourceContext("work"), CancellationToken.None);
            var port = FreePort();
            var provider = new SmbMountProvider();
            Assert.IsNull(provider.UnavailableReason);
            var settings = (SmbMountSettings)provider.CreateDefaultSettings(new Collection("work"));
            settings.Port = port;
            settings.User = "user";
            settings.Password = "password";
            var mount = provider.CreateMount(settings, new Collection("work"));
            var context = new TestMountContext("work", "smb");
            await mount.StartAsync(new SingleSourceVfs(source, "work"), context, CancellationToken.None);
            _cleanup.Add(mount);
            Assert.IsTrue(mount.AccessPaths.Count >= 2, string.Join(" | ", mount.AccessPaths.Select(p => p.Value)));
            StringAssert.Contains(mount.AccessPaths[0].Value, "localhost");
            Assert.IsFalse(mount.AccessPaths.Any(p => p.Value.Contains("password")), "the password never appears in an access path");
            Assert.IsNotNull(mount.OpenAction);

            var dialect = await NegotiateAsync(port);
            Assert.IsTrue(dialect is 0x0202 or 0x0210 or 0x0300 or 0x0302, $"dialect 0x{dialect:X4}");

            // a second collection on the same port shares the process and restarts it with both shares
            var other = new MemorySource().AddFile("b.txt", "b");
            await other.InitialiseAsync(new TestSourceContext("other"), CancellationToken.None);
            var second = provider.CreateMount(settings, new Collection("other"));
            await second.StartAsync(new SingleSourceVfs(other, "other"), new TestMountContext("other", "smb", shareWith: context), CancellationToken.None);
            _cleanup.Add(second);
            Assert.AreEqual(1, context.SharedCount, "one server process for both");
            Assert.IsTrue((await NegotiateAsync(port)) is 0x0202 or 0x0210 or 0x0300 or 0x0302, "still up after the restart");

            var different = (SmbMountSettings)provider.CreateDefaultSettings(new Collection("third"));
            different.Port = port;
            different.User = "someone-else";
            different.Password = "pw";
            var third = provider.CreateMount(different, new Collection("third"));
            var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => third.StartAsync(new SingleSourceVfs(other, "third"), new TestMountContext("third", "smb", shareWith: context), CancellationToken.None));
            StringAssert.Contains(ex.Message, "same login");
        }
        finally
        {
            Environment.SetEnvironmentVariable(SmbServerBinary.EnvironmentVariable, null);
        }
    }

    [TestMethod]
    public void SettingsValidationAndCommands()
    {
        var provider = new SmbMountProvider();
        Assert.IsTrue(provider.Validate(new SmbMountSettings()).Any(m => m.IsError && m.Property == nameof(SmbMountSettings.Password)), "no password yet");
        Assert.AreEqual(0, provider.Validate(new SmbMountSettings { Password = "pw" }).Count(m => m.IsError));
        Assert.AreEqual(0, provider.Validate(new SmbMountSettings { Guest = true, User = null }).Count(m => m.IsError), "guest needs no login");
        Assert.IsTrue(provider.Validate(new SmbMountSettings { Guest = true, BindAddress = "0.0.0.0" }).Any(m => !m.IsError), "guest on the LAN warns");
        Assert.IsTrue(provider.Validate(new SmbMountSettings { Password = "pw", Port = 445 }).Any(m => !m.IsError), "445 warns");

        var user = new SmbCredentialsView("alice", "pw", false);
        CollectionAssert.AreEqual(new[] { "use", @"\\localhost\work", "/TCPPORT:1445", "/USER:alice", "pw" }, SmbMountProvider.WindowsConnectArguments(@"\\localhost\work", 1445, user).ToList());
        CollectionAssert.AreEqual(new[] { "use", @"\\localhost\work", "/TCPPORT:1445" }, SmbMountProvider.WindowsConnectArguments(@"\\localhost\work", 1445, new SmbCredentialsView(null, null, true)).ToList());
        CollectionAssert.AreEqual(new[] { "use", "Y:", @"\\localhost\work", "/TCPPORT:1445", "/USER:alice", "pw" }, SmbMountProvider.WindowsConnectArguments(@"\\localhost\work", 1445, user, "Y").ToList());
        Assert.AreEqual("sudo mount -t cifs //nas/work /mnt/work -o port=1445,vers=3.0,user=alice", SmbMountProvider.LinuxMountCommand("nas", 1445, "work", user));
        Assert.AreEqual("smb://alice@nas:1445/work", SmbMountProvider.MacUrl("nas", 1445, "work", user));

        if (OperatingSystem.IsWindows())
        {
            Assert.IsTrue(provider.Validate(new SmbMountSettings { Password = "pw", DriveLetter = "1" }).Any(m => m.IsError && m.Property == nameof(SmbMountSettings.DriveLetter)), "a bad letter is refused");
            Assert.AreEqual(0, provider.Validate(new SmbMountSettings { Password = "pw", DriveLetter = "y:" }).Count(m => m.IsError));
            Assert.IsNull(WindowsDriveMapping.CurrentTarget("C:"), "a local disk is not a network connection");
        }
        else
        {
            Assert.AreEqual(0, provider.Validate(new SmbMountSettings { Password = "pw", DriveLetter = "1" }).Count(m => m.IsError), "the letter is a Windows setting; elsewhere it is ignored");
        }
    }

    /// <summary>Spec 8.2 with the opt-in letter: net use maps it to the running server and the stop removes it (needs Windows 11 24H2's /TCPPORT).</summary>
    [TestMethod]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task ADriveLetterIsMappedWhileTheMountRunsOnWindows()
    {
        if (!OperatingSystem.IsWindows() || Environment.OSVersion.Version.Build < 26100)
        {
            Assert.Inconclusive("net use /TCPPORT needs Windows 11 24H2");
            return;
        }

        var binary = FindServerBinary();
        if (binary is null)
        {
            Assert.Inconclusive("portable-smb-server binary not found: run scripts/fetch-go-servers.ps1 or set ANYFS_SMB_SERVER");
        }

        var letter = "ZYXWVUTSRQPONMLKJIHGFED".Select(c => c.ToString()).FirstOrDefault(l => !Directory.Exists(l + @":\") && WindowsDriveMapping.CurrentTarget(l + ":") is null);
        if (letter is null)
        {
            Assert.Inconclusive("no free drive letter");
        }

        Environment.SetEnvironmentVariable(SmbServerBinary.EnvironmentVariable, binary);
        try
        {
            var source = Tree();
            await source.InitialiseAsync(new TestSourceContext("work"), CancellationToken.None);
            var provider = new SmbMountProvider();
            var settings = (SmbMountSettings)provider.CreateDefaultSettings(new Collection("work"));
            settings.Port = FreePort();
            settings.User = "user";
            settings.Password = "password";
            settings.DriveLetter = letter;
            var mount = provider.CreateMount(settings, new Collection("work"));
            var context = new TestMountContext("work", "smb");
            await mount.StartAsync(new SingleSourceVfs(source, "work"), context, CancellationToken.None);
            _cleanup.Add(mount);
            Assert.AreEqual(0, context.HealthReports.Count, string.Join(" | ", context.HealthReports.Select(h => h.Detail)));
            Assert.IsTrue(mount.AccessPaths.Any(p => p.Value == letter + @":\"), string.Join(" | ", mount.AccessPaths.Select(p => p.Value)));
            Assert.IsInstanceOfType<MountOpenAction.FileManager>(mount.OpenAction);
            Assert.AreEqual(letter + @":\", ((MountOpenAction.FileManager)mount.OpenAction!).Path);
            Assert.AreEqual("hello readme", File.ReadAllText(Path.Combine(letter + @":\", "readme.txt")), "the share is readable through the letter");

            await mount.StopAsync(CancellationToken.None);
            Assert.IsNull(WindowsDriveMapping.CurrentTarget(letter + ":"), "the letter is gone once the mount stops");
        }
        finally
        {
            Environment.SetEnvironmentVariable(SmbServerBinary.EnvironmentVariable, null);
        }
    }

    /// <summary>A guest share gets no letter where Windows blocks guest logons, and anyfs says so at once (net use would take twenty seconds to).</summary>
    [TestMethod]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task AGuestShareGetsNoLetterWhereWindowsBlocksGuestLogons()
    {
        if (!OperatingSystem.IsWindows() || WindowsDriveMapping.GuestLogonsAllowed())
        {
            Assert.Inconclusive("Windows with guest logons blocked only");
            return;
        }

        var outcome = await WindowsDriveMapping.MapAsync("Q", @"\\localhost\nothing", ["use", "Q:", @"\\localhost\nothing", "/TCPPORT:1"], guest: true, NullLogger.Instance, CancellationToken.None);
        Assert.IsFalse(outcome.Success);
        StringAssert.Contains(outcome.Message, "guest");
        StringAssert.Contains(outcome.Message, "user name and password");
    }

    /// <summary>tools/go-servers/&lt;rid&gt;/ in the repo (walking up from the test binaries), or ANYFS_SMB_SERVER.</summary>
    private static string? FindServerBinary()
    {
        var env = Environment.GetEnvironmentVariable(SmbServerBinary.EnvironmentVariable);
        if (!string.IsNullOrEmpty(env) && File.Exists(env))
        {
            return env;
        }

        var rid = OperatingSystem.IsWindows() ? "win-x64" : OperatingSystem.IsMacOS() ? (System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "osx-arm64" : "osx-x64") : (System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "linux-arm64" : "linux-x64");
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AnyFS.slnx")))
            {
                var candidate = Path.Combine(dir.FullName, "tools", "go-servers", rid, SmbServerBinary.FileName);
                return File.Exists(candidate) ? candidate : null;
            }
        }

        return null;
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>An SMB2 NEGOTIATE over a NetBIOS session: the dialect the server picked, which proves it is serving.</summary>
    private static async Task<int> NegotiateAsync(int port)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        var body = new byte[64 + 40];
        body[0] = 0xFE;
        body[1] = (byte)'S';
        body[2] = (byte)'M';
        body[3] = (byte)'B';
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(4), 64); // StructureSize
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(12), 0); // Command: NEGOTIATE
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(14), 1); // CreditRequest
        var n = 64;
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(n), 36); // StructureSize
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(n + 2), 2); // DialectCount
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(n + 4), 1); // SecurityMode: signing enabled
        Guid.NewGuid().TryWriteBytes(body.AsSpan(n + 12)); // ClientGuid
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(n + 36), 0x0202);
        BinaryPrimitives.WriteUInt16LittleEndian(body.AsSpan(n + 38), 0x0210);
        var frame = new byte[4 + body.Length];
        frame[1] = (byte)(body.Length >> 16);
        frame[2] = (byte)(body.Length >> 8);
        frame[3] = (byte)body.Length;
        body.CopyTo(frame, 4);
        await stream.WriteAsync(frame);

        var header = new byte[4];
        await ReadExactlyAsync(stream, header);
        var length = (header[1] << 16) | (header[2] << 8) | header[3];
        var response = new byte[length];
        await ReadExactlyAsync(stream, response);
        Assert.IsTrue(response.Length >= 70 && response[0] == 0xFE && response[1] == 'S' && response[2] == 'M' && response[3] == 'B', "an SMB2 response");
        Assert.AreEqual(0u, BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(8)), "STATUS_SUCCESS");
        return BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(64 + 4));
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await stream.ReadExactlyAsync(buffer, timeout.Token);
    }

    private sealed class ClientDisposer(HttpClient client) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            client.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
