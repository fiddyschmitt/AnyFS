using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using AnyFS.Mounts.Smb;
using AnyFS.Sdk;
using AnyFS.Sdk.Testing;
using LibSmb.Protocol;
using LibSmb.Tests;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnyFS.Mounts.Tests;

/// <summary>
/// Spec 8.2 and 16.1: the SMB mount, LibSmb serving collections in this process, signed in to and used over the wire with
/// LibSmb's test client: shares sharing a port, reads and writes through the VFS, read-only collections and files, a
/// failed read, the stats; and on Windows 11 24H2 the drive letter, through Windows' own client.
/// </summary>
[TestClass]
public class SmbMountTests
{
    private const string User = "user";
    private const string Password = "password";
    private readonly List<IAsyncDisposable> _cleanup = [];

    [TestCleanup]
    public async Task Cleanup()
    {
        foreach (var d in Enumerable.Reverse(_cleanup))
        {
            await d.DisposeAsync();
        }
    }

    private static MemorySource Tree(bool canWrite = true) => new MemorySource(canWrite)
        .AddFile("readme.txt", "hello readme")
        .AddFile("docs/a.txt", "aaa")
        .AddFile("medium.bin", Medium, new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero))
        .AddDirectory("emptydir");

    private static byte[] Medium { get; } = [.. Enumerable.Range(0, 50_000).Select(i => (byte)(i * 31 % 253))];

    private sealed record Collection(string Name) : ICollectionInfo
    {
        public Guid Id { get; } = Guid.NewGuid();
    }

    /// <summary>A collection mounted over SMB on a free port with the test login; the mount, its context and the port.</summary>
    private async Task<(IMount Mount, TestMountContext Context, int Port)> MountAsync(IVirtualFileSystem fs, string collection = "work", int? port = null, TestMountContext? shareWith = null, bool guest = false)
    {
        var provider = new SmbMountProvider();
        var settings = (SmbMountSettings)provider.CreateDefaultSettings(new Collection(collection));
        settings.Port = port ?? FreePort();
        settings.Guest = guest;
        settings.User = guest ? null : User;
        settings.Password = guest ? null : Password;
        var mount = provider.CreateMount(settings, new Collection(collection));
        var context = new TestMountContext(collection, "smb", shareWith: shareWith);
        await mount.StartAsync(fs, context, CancellationToken.None);
        _cleanup.Add(mount);
        return (mount, context, settings.Port);
    }

    private static async Task<SingleSourceVfs> VfsAsync(MemorySource source, string collection = "work", bool readOnly = false)
    {
        await source.InitialiseAsync(new TestSourceContext(collection), CancellationToken.None);
        return new SingleSourceVfs(source, collection, readOnly);
    }

    private async Task<(SmbTestClient Client, uint Tree)> ConnectAsync(int port, string share = "work", string? user = User, string? password = Password)
    {
        var client = await SmbTestClient.SignInAsync(new IPEndPoint(IPAddress.Loopback, port), user, password);
        _cleanup.Add(client);
        return (client, await client.TreeConnectAsync(share));
    }

    /// <summary>
    /// The mount serves its collection in this process, signed in to with its login: listed and read over the wire. A second
    /// collection on the port shares the server; a third with another login is refused; a collection unmounted is gone from
    /// the server while the other carries on, and the last one takes the server with it.
    /// </summary>
    [TestMethod]
    public async Task CollectionsOnAPortShareOneServerInThisProcess()
    {
        var (mount, context, port) = await MountAsync(await VfsAsync(Tree()));
        Assert.IsNull(new SmbMountProvider().UnavailableReason, "always there: nothing to unpack or fetch");
        Assert.IsGreaterThanOrEqualTo(2, mount.AccessPaths.Count, string.Join(" | ", mount.AccessPaths.Select(p => p.Value)));
        StringAssert.Contains(mount.AccessPaths[0].Value, "localhost");
        Assert.IsFalse(mount.AccessPaths.Any(p => p.Value.Contains(Password)), "the password never appears in an access path");
        Assert.IsNotNull(mount.OpenAction);
        if (OperatingSystem.IsWindows())
        {
            // no drive letter here, so Open is the device-less net use; it must drop a stale connection first so a
            // changed password reconnects instead of failing with "incorrect password"
            var command = mount.OpenAction as MountOpenAction.Command;
            Assert.IsNotNull(command, mount.OpenAction!.GetType().Name);
            CollectionAssert.AreEqual(new[] { "use", command.ThenOpen, "/delete" }, command.Reset!.ToList());
        }

        var (client, tree) = await ConnectAsync(port);
        CollectionAssert.AreEquivalent(new[] { "readme.txt", "docs", "medium.bin", "emptydir" }, await client.ListAsync(tree, ""));
        CollectionAssert.AreEqual(Medium, await ReadAsync(client, tree, "medium.bin"));
        Assert.IsTrue(client.Dialect is Dialect.Smb302, $"dialect 0x{client.Dialect:X4}");

        var (second, _, _) = await MountAsync(await VfsAsync(new MemorySource().AddFile("b.txt", "b"), "other"), "other", port, context);
        Assert.AreEqual(1, context.SharedCount, "one server for both");
        var (otherClient, otherTree) = await ConnectAsync(port, "other");
        Assert.AreEqual("b", Encoding.UTF8.GetString(await ReadAsync(otherClient, otherTree, "b.txt")));

        var provider = new SmbMountProvider();
        var different = (SmbMountSettings)provider.CreateDefaultSettings(new Collection("third"));
        different.Port = port;
        different.User = "someone-else";
        different.Password = "pw";
        var third = provider.CreateMount(different, new Collection("third"));
        var refused = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => third.StartAsync(new SingleSourceVfs(new MemorySource(), "third"), new TestMountContext("third", "smb", shareWith: context), CancellationToken.None));
        StringAssert.Contains(refused.Message, "same login");

        await second.StopAsync(CancellationToken.None);
        Assert.AreEqual(Status.BadNetworkName, (await client.SendAsync(Command.TreeConnect, TreeConnectBody("other"))).Status, "the unmounted collection is no share now");
        Assert.AreEqual(Status.NetworkNameDeleted, (await otherClient.CreateAsync(otherTree, "b.txt")).Status);
        Assert.AreEqual("hello readme", Encoding.UTF8.GetString(await ReadAsync(client, tree, "readme.txt")), "the other collection carries on");

        await mount.StopAsync(CancellationToken.None);
        using var probe = new TcpClient();
        await Assert.ThrowsAsync<SocketException>(() => probe.ConnectAsync(IPAddress.Loopback, port), "the last mount takes the server with it");
    }

    /// <summary>
    /// Writes go through the VFS: a new file, a folder, a rename, a delete; an OPEN_IF of an existing file opens it rather
    /// than truncating it (as the Go server's VFS client did, through the endpoint's create), and an overwrite truncates.
    /// </summary>
    [TestMethod]
    public async Task WritesGoThroughTheVfs()
    {
        var source = Tree();
        var (_, _, port) = await MountAsync(await VfsAsync(source));
        var (client, tree) = await ConnectAsync(port);

        var created = await client.OpenAsync(tree, "new.txt", write: true, disposition: Disposition.Create);
        (await client.WriteAsync(tree, created, 0, "written"u8.ToArray())).EnsureSuccess();
        (await client.CloseAsync(tree, created)).EnsureSuccess();
        Assert.AreEqual("written", Encoding.UTF8.GetString(source.GetFile("new.txt")!));

        var openIf = await client.OpenAsync(tree, "readme.txt", write: true, disposition: Disposition.OpenIf);
        (await client.WriteAsync(tree, openIf, 0, "HELLO"u8.ToArray())).EnsureSuccess();
        (await client.CloseAsync(tree, openIf)).EnsureSuccess();
        Assert.AreEqual("HELLO readme", Encoding.UTF8.GetString(source.GetFile("readme.txt")!), "OPEN_IF keeps what is there");

        var overwrite = await client.OpenAsync(tree, "docs/a.txt", write: true, disposition: Disposition.OverwriteIf);
        (await client.WriteAsync(tree, overwrite, 0, "b"u8.ToArray())).EnsureSuccess();
        (await client.CloseAsync(tree, overwrite)).EnsureSuccess();
        Assert.AreEqual("b", Encoding.UTF8.GetString(source.GetFile("docs/a.txt")!), "OVERWRITE_IF truncates");

        var folder = SmbTestClient.FileIdOf((await client.CreateAsync(tree, "made", 0x00100081, Disposition.Create, CreateOptions.DirectoryFile)).EnsureSuccess());
        await client.CloseAsync(tree, folder);
        Assert.IsTrue(source.Exists("made"));

        var moving = SmbTestClient.FileIdOf((await client.CreateAsync(tree, "new.txt", Access.Delete | 0x00100080)).EnsureSuccess());
        (await client.RenameAsync(tree, moving, @"made\moved.txt")).EnsureSuccess();
        await client.CloseAsync(tree, moving);
        Assert.AreEqual("written", Encoding.UTF8.GetString(source.GetFile("made/moved.txt")!));

        var deleting = SmbTestClient.FileIdOf((await client.CreateAsync(tree, @"made\moved.txt", Access.Delete | 0x00100080)).EnsureSuccess());
        (await client.DeleteOnCloseAsync(tree, deleting)).EnsureSuccess();
        (await client.CloseAsync(tree, deleting)).EnsureSuccess();
        Assert.IsFalse(source.Exists("made/moved.txt"));
    }

    /// <summary>A read-only collection is a read-only share: read-only access in TREE_CONNECT, writes refused, reads served.</summary>
    [TestMethod]
    public async Task AReadOnlyCollectionIsAReadOnlyShare()
    {
        var source = Tree();
        var (_, _, port) = await MountAsync(await VfsAsync(source, readOnly: true));
        var client = await SmbTestClient.SignInAsync(new IPEndPoint(IPAddress.Loopback, port), User, Password);
        _cleanup.Add(client);
        var tree = await client.SendAsync(Command.TreeConnect, TreeConnectBody("work"));
        Assert.AreEqual(Access.ReadExecute, Wire.U32(tree.Body, 12), "MaximalAccess");
        Assert.AreEqual(Status.MediaWriteProtected, (await client.CreateAsync(tree.TreeId, "readme.txt", Access.FileWriteData, Disposition.OpenIf)).Status);
        Assert.AreEqual(Status.MediaWriteProtected, (await client.CreateAsync(tree.TreeId, "new.txt", 0x00120089, Disposition.OpenIf)).Status);
        Assert.AreEqual("hello readme", Encoding.UTF8.GetString(await ReadAsync(client, tree.TreeId, "readme.txt")));
        Assert.IsFalse(source.Exists("new.txt"));
    }

    /// <summary>
    /// A file read-only itself shows FILE_ATTRIBUTE_READONLY and refuses write opens; a file that is read-only only because
    /// its source cannot write carries no flag (every copy of it would keep one), and its writes are refused when tried.
    /// </summary>
    [TestMethod]
    public async Task ReadOnlyFilesAndReadOnlySources()
    {
        var source = Tree();
        var (_, _, port) = await MountAsync(new ReadOnlyFile(await VfsAsync(source), "readme.txt"));
        var (client, tree) = await ConnectAsync(port);
        var readme = await client.OpenAsync(tree, "readme.txt");
        var basic = await client.QueryInfoAsync(tree, readme, InfoType.File, FileInfoClass.Basic);
        Assert.AreEqual(FileAttribute.ReadOnly, Wire.U32(basic.Body, 8 + 32));
        Assert.AreEqual(Status.AccessDenied, (await client.CreateAsync(tree, "readme.txt", Access.FileWriteData)).Status);

        var locked = Tree(canWrite: false);
        var (_, _, lockedPort) = await MountAsync(await VfsAsync(locked, "locked"), "locked");
        var (lockedClient, lockedTree) = await ConnectAsync(lockedPort, "locked");
        var file = await lockedClient.OpenAsync(lockedTree, "readme.txt");
        basic = await lockedClient.QueryInfoAsync(lockedTree, file, InfoType.File, FileInfoClass.Basic);
        Assert.AreEqual(FileAttribute.Normal, Wire.U32(basic.Body, 8 + 32), "no flag for every copy to keep");
        var write = await lockedClient.CreateAsync(lockedTree, "readme.txt", Access.FileWriteData);
        Assert.IsTrue(Status.IsError(write.Status), $"0x{write.Status:X8}: the write open is refused");
        Assert.AreEqual(Status.MediaWriteProtected, write.Status, "by the read-only source");
    }

    /// <summary>
    /// A read that fails (a download that broke off) fails that request; the client opens the file again and reads afresh,
    /// since an open's read-ahead goes with it.
    /// </summary>
    [TestMethod]
    public async Task AFailedReadIsTriedAgainOnAFreshOpen()
    {
        var flaky = new FirstOpenFailsReads(await VfsAsync(new MemorySource().AddFile("readme.txt", "hello readme")));
        var (_, _, port) = await MountAsync(flaky);
        var (client, tree) = await ConnectAsync(port);
        var first = await client.OpenAsync(tree, "readme.txt");
        Assert.AreEqual(Status.Unsuccessful, (await client.ReadRawAsync(tree, first, 0, 5)).Status);
        await client.CloseAsync(tree, first);
        Assert.AreEqual("hello readme", Encoding.UTF8.GetString(await ReadAsync(client, tree, "readme.txt")));
        Assert.AreEqual(2, flaky.Opens);
    }

    /// <summary>A sequential reader in 64 KiB pieces reaches the source in 1 MiB ones (the read-ahead WinFsp and FUSE have too).</summary>
    [TestMethod]
    public async Task SequentialReadsReachTheSourceInBigPieces()
    {
        var big = new byte[3 << 20];
        new Random(7).NextBytes(big);
        var counting = new CountingReads(await VfsAsync(new MemorySource().AddFile("big.bin", big)));
        var (_, _, port) = await MountAsync(counting);
        var (client, tree) = await ConnectAsync(port);
        CollectionAssert.AreEqual(big, await ReadAsync(client, tree, "big.bin"));
        Assert.IsLessThanOrEqualTo(6, counting.Reads, $"{counting.Reads} reads of the source for 48 SMB READs");
    }

    /// <summary>Every call into the VFS is a connection in the stats while it runs, and none is left counted after.</summary>
    [TestMethod]
    public async Task CallsAreCountedAsConnections()
    {
        var (_, context, port) = await MountAsync(await VfsAsync(Tree()));
        var (client, tree) = await ConnectAsync(port);
        await client.ListAsync(tree, "");
        await ReadAsync(client, tree, "medium.bin");
        Assert.AreEqual(0, context.ActiveConnections);
    }

    /// <summary>The copies of portable-smb-server earlier builds unpacked into the provider's cache folder go at the next start.</summary>
    [TestMethod]
    public async Task OldServerCopiesAreRemoved()
    {
        var provider = new SmbMountProvider();
        var settings = (SmbMountSettings)provider.CreateDefaultSettings(new Collection("work"));
        settings.Port = FreePort();
        settings.Password = Password;
        var context = new TestMountContext("work", "smb");
        Directory.CreateDirectory(context.ProviderCacheDirectory);
        var old = Path.Combine(context.ProviderCacheDirectory, "portable-smb-server-0123abcd.exe");
        var other = Path.Combine(context.ProviderCacheDirectory, "keep.txt");
        await File.WriteAllTextAsync(old, "old");
        await File.WriteAllTextAsync(other, "keep");
        var mount = provider.CreateMount(settings, new Collection("work"));
        await mount.StartAsync(await VfsAsync(Tree()), context, CancellationToken.None);
        _cleanup.Add(mount);
        Assert.IsFalse(File.Exists(old));
        Assert.IsTrue(File.Exists(other));
    }

    /// <summary>A port another program holds: the mount fails saying so.</summary>
    [TestMethod]
    public async Task APortInUseIsSaid()
    {
        using var holder = new TcpListener(IPAddress.Loopback, 0);
        holder.Start();
        var port = ((IPEndPoint)holder.LocalEndpoint).Port;
        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => MountAsync(new SingleSourceVfs(new MemorySource(), "work"), port: port));
        StringAssert.Contains(ex.Message, $"cannot listen on 127.0.0.1:{port}");
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

    /// <summary>
    /// Spec 8.2 with the opt-in letter, through Windows' own SMB client: net use maps it to the server, the share reads and
    /// writes through it, and the stop removes it (Windows 11 24H2's /TCPPORT).
    /// </summary>
    [TestMethod]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task ADriveLetterIsMappedWhileTheMountRunsOnWindows()
    {
        if (!OperatingSystem.IsWindows() || Environment.OSVersion.Version.Build < 26100)
        {
            Assert.Inconclusive("net use /TCPPORT needs Windows 11 24H2");
            return;
        }

        var letter = "ZYXWVUTSRQPONMLKJIHGFED".Select(c => c.ToString()).FirstOrDefault(l => !Directory.Exists(l + @":\") && WindowsDriveMapping.CurrentTarget(l + ":") is null);
        if (letter is null)
        {
            Assert.Inconclusive("no free drive letter");
        }

        var source = Tree();
        var provider = new SmbMountProvider();
        var settings = (SmbMountSettings)provider.CreateDefaultSettings(new Collection("work"));
        settings.Port = FreePort();
        settings.User = User;
        settings.Password = Password;
        settings.DriveLetter = letter;
        var mount = provider.CreateMount(settings, new Collection("work"));
        var context = new TestMountContext("work", "smb");
        await mount.StartAsync(await VfsAsync(source), context, CancellationToken.None);
        _cleanup.Add(mount);
        Assert.AreEqual(0, context.HealthReports.Count, string.Join(" | ", context.HealthReports.Select(h => h.Detail)));
        Assert.IsTrue(mount.AccessPaths.Any(p => p.Value == letter + @":\"), string.Join(" | ", mount.AccessPaths.Select(p => p.Value)));
        Assert.IsInstanceOfType<MountOpenAction.FileManager>(mount.OpenAction);
        Assert.AreEqual(letter + @":\", ((MountOpenAction.FileManager)mount.OpenAction!).Path);

        var root = letter + @":\";
        Assert.AreEqual("hello readme", File.ReadAllText(Path.Combine(root, "readme.txt")), "the share is readable through the letter");
        CollectionAssert.AreEqual(Medium, File.ReadAllBytes(Path.Combine(root, "medium.bin")));
        CollectionAssert.AreEquivalent(new[] { "readme.txt", "docs", "medium.bin", "emptydir" }, Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName).ToList());
        File.WriteAllText(Path.Combine(root, "from-windows.txt"), "written by Windows");
        Assert.AreEqual("written by Windows", Encoding.UTF8.GetString(source.GetFile("from-windows.txt")!));
        File.Move(Path.Combine(root, "from-windows.txt"), Path.Combine(root, "docs", "moved.txt"));
        Directory.CreateDirectory(Path.Combine(root, "made"));
        File.Delete(Path.Combine(root, "docs", "moved.txt"));
        Assert.IsFalse(source.Exists("docs/moved.txt"));
        Assert.IsTrue(source.Exists("made"));

        await mount.StopAsync(CancellationToken.None);
        Assert.IsNull(WindowsDriveMapping.CurrentTarget(letter + ":"), "the letter is gone once the mount stops");
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

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static byte[] TreeConnectBody(string share)
    {
        var path = Encoding.Unicode.GetBytes(@"\\localhost\" + share);
        var body = new byte[8 + path.Length];
        Wire.Put16(body, 0, 9);
        Wire.Put16(body, 4, Smb2.HeaderSize + 8);
        Wire.Put16(body, 6, (ushort)path.Length);
        path.CopyTo(body, 8);
        return body;
    }

    private static async Task<byte[]> ReadAsync(SmbTestClient client, uint tree, string name)
    {
        var fileId = await client.OpenAsync(tree, name);
        var data = new List<byte>();
        while (true)
        {
            var piece = await client.ReadAsync(tree, fileId, data.Count, 65536);
            if (piece.Length == 0)
            {
                break;
            }

            data.AddRange(piece);
        }

        (await client.CloseAsync(tree, fileId)).EnsureSuccess();
        return [.. data];
    }

    /// <summary>A collection passed through, changed by the subclass where it says.</summary>
    private abstract class Passthrough(IVirtualFileSystem inner) : IVirtualFileSystem
    {
        protected IVirtualFileSystem Inner => inner;

        public string CollectionName => inner.CollectionName;

        public bool ReadOnly => inner.ReadOnly;

        public bool CanWrite(string path) => inner.CanWrite(path);

        public virtual Task<VfsEntry?> StatAsync(string path, CancellationToken ct) => inner.StatAsync(path, ct);

        public virtual IAsyncEnumerable<VfsEntry> ListAsync(string path, CancellationToken ct) => inner.ListAsync(path, ct);

        public virtual Task<IVfsFile> OpenAsync(string path, VfsOpenMode mode, CancellationToken ct) => inner.OpenAsync(path, mode, ct);

        public Task CreateDirectoryAsync(string path, CancellationToken ct) => inner.CreateDirectoryAsync(path, ct);

        public Task DeleteAsync(string path, CancellationToken ct) => inner.DeleteAsync(path, ct);

        public Task RenameAsync(string fromPath, string toPath, bool replace, CancellationToken ct) => inner.RenameAsync(fromPath, toPath, replace, ct);

        public Task SetTimesAsync(string path, DateTimeOffset? created, DateTimeOffset? modified, CancellationToken ct) => inner.SetTimesAsync(path, created, modified, ct);

        public Task<VfsSpace?> GetSpaceAsync(string path, CancellationToken ct) => inner.GetSpaceAsync(path, ct);
    }

    /// <summary>A collection where one file is read-only itself.</summary>
    private sealed class ReadOnlyFile(IVirtualFileSystem inner, string path) : Passthrough(inner)
    {
        public override async Task<VfsEntry?> StatAsync(string p, CancellationToken ct) => Mark(p, await Inner.StatAsync(p, ct));

        public override async IAsyncEnumerable<VfsEntry> ListAsync(string p, [EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var entry in Inner.ListAsync(p, ct))
            {
                yield return Mark(VfsPath.Join(p, entry.Name), entry)!;
            }
        }

        private VfsEntry? Mark(string p, VfsEntry? entry) => entry is not null && p == path ? entry with { Attributes = entry.Attributes | VfsAttributes.ReadOnly } : entry;
    }

    /// <summary>A collection whose first opened file fails every read, as a source whose download broke off would; later opens are fine.</summary>
    private sealed class FirstOpenFailsReads(IVirtualFileSystem inner) : Passthrough(inner)
    {
        private int _opens;

        public int Opens => _opens;

        public override async Task<IVfsFile> OpenAsync(string path, VfsOpenMode mode, CancellationToken ct)
        {
            var file = await Inner.OpenAsync(path, mode, ct);
            return Interlocked.Increment(ref _opens) == 1 ? new Broken(file) : file;
        }

        private sealed class Broken(IVfsFile file) : IVfsFile
        {
            public long Length => file.Length;

            public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct) => throw new IOException("the download broke off");

            public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct) => file.WriteAsync(offset, data, ct);

            public ValueTask SetLengthAsync(long length, CancellationToken ct) => file.SetLengthAsync(length, ct);

            public ValueTask FlushAsync(CancellationToken ct) => file.FlushAsync(ct);

            public ValueTask DisposeAsync() => file.DisposeAsync();
        }
    }

    /// <summary>A collection that counts the reads its files are asked for.</summary>
    private sealed class CountingReads(IVirtualFileSystem inner) : Passthrough(inner)
    {
        private int _reads;

        public int Reads => _reads;

        public override async Task<IVfsFile> OpenAsync(string path, VfsOpenMode mode, CancellationToken ct) => new Counted(await Inner.OpenAsync(path, mode, ct), this);

        private sealed class Counted(IVfsFile file, CountingReads owner) : IVfsFile
        {
            public long Length => file.Length;

            public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct)
            {
                Interlocked.Increment(ref owner._reads);
                return file.ReadAsync(offset, buffer, ct);
            }

            public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken ct) => file.WriteAsync(offset, data, ct);

            public ValueTask SetLengthAsync(long length, CancellationToken ct) => file.SetLengthAsync(length, ct);

            public ValueTask FlushAsync(CancellationToken ct) => file.FlushAsync(ct);

            public ValueTask DisposeAsync() => file.DisposeAsync();
        }
    }
}
