using System.Runtime.CompilerServices;
using System.Text;
using LibSmb.Protocol;
using static LibSmb.Tests.TestSupport;

namespace LibSmb.Tests;

/// <summary>
/// Apple's extensions, lookups by name and read-only shares, as portable-smb-server's Go tests have them
/// (aapl_test.go, lookup_test.go, readonly_test.go), ported.
/// </summary>
[TestClass]
public class ShareBehaviourTests
{
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

    private SmbServer Track(SmbServer server)
    {
        _servers.Add(server);
        return server;
    }

    /// <summary>A CREATE carrying an AAPL server query, the way macOS opens the share's root after a tree connect.</summary>
    private static byte[] AppleCreateBody(string name, ulong bitmap, ulong clientCaps)
    {
        var query = new byte[24];
        Wire.Put32(query, 0, Apple.ServerQuery);
        Wire.Put64(query, 8, bitmap);
        Wire.Put64(query, 16, clientCaps);
        return SmbTestClient.CreateBody(name, 0x00120089, Disposition.Open, 0, Apple.ContextBlob(Apple.ContextName, query));
    }

    /// <summary>
    /// The AAPL answer comes back as a create context (ReadDirAttr granted, the volume's capabilities, the model) and the
    /// connection remembers it; a client without ReadDirAttr gets UNIX-based only; a CREATE without contexts keeps the
    /// plain 89-byte response.
    /// </summary>
    [TestMethod]
    public async Task TheAppleCreateContextIsAnswered()
    {
        var server = Track(Server(Dir()));
        var c = Connection(server);
        var tree = await TreeConnectAsync(c);
        var (status, reply) = await c.CreateAsync(AppleCreateBody("", Apple.ServerCaps | Apple.VolumeCaps | Apple.ModelInfo, Apple.SupportsReadDirAttr | Apple.UnixBased), tree, None);
        using (reply)
        {
            Assert.AreEqual(Status.Success, status);
            Assert.IsTrue(c.AppleReadDirAttr);
            var body = reply.Body.ToArray();
            var offset = (int)Wire.U32(body, 80);
            var length = (int)Wire.U32(body, 84);
            Assert.AreEqual(Smb2.HeaderSize + 88, offset);
            Assert.AreEqual(body.Length, 88 + length, "the contexts fill the body");
            var context = body.AsSpan(88);
            Assert.AreEqual(Apple.ContextName, Encoding.ASCII.GetString(context.Slice(Wire.U16(context, 4), Wire.U16(context, 6))));
            var data = context.Slice(Wire.U16(context, 10), (int)Wire.U32(context, 12));
            Assert.AreEqual(Apple.ServerQuery, Wire.U32(data, 0));
            Assert.AreEqual(Apple.ServerCaps | Apple.VolumeCaps | Apple.ModelInfo, Wire.U64(data, 8));
            Assert.AreEqual(Apple.SupportsReadDirAttr | Apple.UnixBased, Wire.U64(data, 16));
            var caseSensitive = !server.Shares.Single().FileSystem.CaseInsensitive;
            Assert.AreEqual(caseSensitive, (Wire.U64(data, 24) & Apple.CaseSensitive) != 0);
            Assert.AreEqual(0u, Wire.U32(data, 32));
            Assert.AreEqual(Apple.Model, Wire.Utf16(data.Slice(40, (int)Wire.U32(data, 36))));
        }

        var plain = Connection(server);
        tree = await TreeConnectAsync(plain);
        (status, reply) = await plain.CreateAsync(AppleCreateBody("", Apple.ServerCaps, 0), tree, None);
        using (reply)
        {
            Assert.AreEqual(Status.Success, status);
            Assert.IsFalse(plain.AppleReadDirAttr, "a client without ReadDirAttr must not get it");
            Assert.AreEqual(Apple.UnixBased, Wire.U64(reply.Body, 88 + 24 + 16));
        }

        (status, reply) = await plain.CreateAsync(SmbTestClient.CreateBody(""), tree, None);
        using (reply)
        {
            Assert.AreEqual(Status.Success, status);
            Assert.AreEqual(89, reply.BodyLength);
            Assert.AreEqual(0u, Wire.U32(reply.Body, 80));
        }
    }

    /// <summary>
    /// A ReadDirAttr client's entries: max access in EaSize (all on a writable share, read and execute on a read-only one or
    /// for a read-only file), ShortNameLength 24, zero fork size and Finder info, the UNIX mode; the FileId and the name where
    /// they always are. A connection without it gets the plain layout.
    /// </summary>
    [TestMethod]
    public async Task ReadDirAttrEntriesHaveApplesLayout()
    {
        var dir = Dir();
        await File.WriteAllTextAsync(Path.Combine(dir, "file.tmp"), "x");
        var locked = Path.Combine(dir, "locked.tmp");
        await File.WriteAllTextAsync(locked, "x");
        File.SetAttributes(locked, System.IO.FileAttributes.ReadOnly);
        try
        {
            foreach (var readOnlyShare in new[] { false, true })
            {
                var server = Track(SmbServer.Start(new SmbServerOptions { EndPoint = new(System.Net.IPAddress.Loopback, 0) }, [new SmbShare("data", new LocalFileSystem(dir), readOnlyShare)]));
                var c = Connection(server);
                c.AppleReadDirAttr = true;
                var fileId = DirHandle(c, server, "");
                var (status, reply) = await c.QueryDirectoryAsync(QueryDirectoryBody(fileId, FileInfoClass.IdBothDirectory), None);
                using (reply)
                {
                    Assert.AreEqual(Status.Success, status);
                    var seen = 0;
                    foreach (var (_, e) in Entries(reply.Body[8..].ToArray()))
                    {
                        var name = NameAt(e, 104);
                        seen++;
                        var readOnly = readOnlyShare || name == "locked.tmp";
                        Assert.AreEqual(readOnly ? Access.ReadExecute : Access.All, Wire.U32(e, 64), $"{name}: max access");
                        Assert.AreEqual(Apple.ShortNameLength, e[68]);
                        Assert.AreEqual(0, e[69]);
                        Assert.IsTrue(e.AsSpan(70, 24).IndexOfAnyExcept((byte)0) < 0, $"{name}: fork size and Finder info");
                        Assert.AreEqual(readOnly ? 0x124 /* 0444 */ : 0x1A4 /* 0644 */, Wire.U16(e, 94), $"{name}: UNIX mode");
                        Assert.AreEqual(OpenFile.PathFileId("data", name), Wire.U64(e, 96));
                    }

                    Assert.AreEqual(2, seen);
                }
            }

            var plainServer = Track(Server(dir));
            var plain = Connection(plainServer);
            var id = DirHandle(plain, plainServer, "");
            var (s, r) = await plain.QueryDirectoryAsync(QueryDirectoryBody(id, FileInfoClass.IdBothDirectory), None);
            using (r)
            {
                Assert.AreEqual(Status.Success, s);
                var e = r.Body[8..].ToArray();
                Assert.IsTrue(e.AsSpan(64, 32).IndexOfAnyExcept((byte)0) < 0, "the plain layout");
            }
        }
        finally
        {
            File.SetAttributes(locked, System.IO.FileAttributes.Normal);
        }
    }

    /// <summary>The request-side parser: a chain of two contexts, an offset past the body, no contexts at all; a blob's size.</summary>
    [TestMethod]
    public void CreateContextsAreParsed()
    {
        var first = Apple.ContextBlob("MxAc", []);
        var second = Apple.ContextBlob(Apple.ContextName, [.. Enumerable.Range(1, 24).Select(i => (byte)i)]);
        Wire.Put32(first, 0, (uint)first.Length);
        byte[] chain = [.. first, .. second];
        var body = new byte[56 + chain.Length];
        Wire.Put32(body, 48, Smb2.HeaderSize + 56);
        Wire.Put32(body, 52, (uint)chain.Length);
        chain.CopyTo(body, 56);
        var contexts = Apple.CreateContexts(body)!;
        Assert.HasCount(2, contexts);
        Assert.IsEmpty(contexts["MxAc"]);
        Assert.HasCount(24, contexts[Apple.ContextName]);
        Assert.AreEqual(24, contexts[Apple.ContextName][23]);

        Wire.Put32(body, 52, (uint)chain.Length + 1000);
        Assert.IsNull(Apple.CreateContexts(body), "a length past the body");
        Assert.IsNull(Apple.CreateContexts(SmbTestClient.CreateBody("x")));
        Assert.HasCount(48, Apple.ContextBlob(Apple.ContextName, new byte[20]), "16 head + 4 name padded to 24, + 20 padded to 24");
    }

    /// <summary>
    /// A QUERY_DIRECTORY whose pattern is a plain name (macOS resolves every entry so, Windows resolves paths so) is one stat
    /// and no listing; the entry is right; a missing name ends the enumeration; a wildcard still lists and filters.
    /// </summary>
    [TestMethod]
    public async Task ALookupByNameIsOneStat()
    {
        var dir = Dir();
        for (var i = 0; i < 2000; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(dir, $"file{i:D4}.txt"), "xy");
        }

        var counting = new CountingFileSystem(new LocalFileSystem(dir));
        var server = Track(SmbServer.Start(new SmbServerOptions { EndPoint = new(System.Net.IPAddress.Loopback, 0) }, [new SmbShare("data", counting)]));
        var c = Connection(server);

        async Task<(uint, byte[])> Query(string pattern, byte flags)
        {
            var fileId = DirHandle(c, server, "");
            var (status, reply) = await c.QueryDirectoryAsync(QueryDirectoryBody(fileId, FileInfoClass.IdBothDirectory, flags, pattern), None);
            using (reply)
            {
                return (status, reply.Body.ToArray());
            }
        }

        var (status, body) = await Query("file1234.txt", 0x03);
        Assert.AreEqual(Status.Success, status);
        Assert.AreEqual(0, counting.Lists);
        Assert.AreEqual(1, counting.Stats);
        var e = body[8..];
        Assert.AreEqual("file1234.txt", NameAt(e, 104));
        Assert.AreEqual(2ul, Wire.U64(e, 40));
        Assert.AreEqual(OpenFile.PathFileId("data", "file1234.txt"), Wire.U64(e, 96));

        (status, _) = await Query("nothere.txt", 0x03);
        Assert.AreEqual(Status.NoMoreFiles, status);
        Assert.AreEqual(0, counting.Lists, "a missing name listed the folder");

        (status, body) = await Query("file12*", 0x01);
        Assert.AreEqual(Status.Success, status);
        Assert.AreEqual(1, counting.Lists);
        Assert.AreEqual(100, Entries(body[8..]).Count());
    }

    /// <summary>
    /// A read-only share refuses every change with MEDIA_WRITE_PROTECTED (write opens, creating a file or a directory,
    /// delete on close, WRITE, rename, delete disposition), advertises read-only access and a read-only volume, and reads.
    /// </summary>
    [TestMethod]
    public async Task AReadOnlyShareRefusesEveryChange()
    {
        var dir = Dir();
        await File.WriteAllTextAsync(Path.Combine(dir, "f.txt"), "hello");
        var server = Track(SmbServer.Start(new SmbServerOptions { EndPoint = new(System.Net.IPAddress.Loopback, 0) }, [new SmbShare("data", new LocalFileSystem(dir), readOnly: true)]));
        var c = Connection(server);
        var treeReply = await HandleAsync(c, Head(Command.TreeConnect), TreeConnectSeg("data"));
        Assert.AreEqual(Status.Success, StatusOf(treeReply));
        Assert.AreEqual(Access.ReadExecute, Wire.U32(treeReply, Smb2.HeaderSize + 12), "MaximalAccess");
        var tree = TreeIdOf(treeReply);

        Assert.AreEqual(Status.MediaWriteProtected, StatusOf(await HandleAsync(c, Head(Command.Create, tree), CreateSeg("f.txt", Access.FileWriteData, Disposition.OpenIf))), "write open");
        Assert.AreEqual(Status.MediaWriteProtected, StatusOf(await HandleAsync(c, Head(Command.Create, tree), CreateSeg("new.txt", 0, Disposition.OpenIf))), "creating a file, even for reading");
        Assert.AreEqual(Status.MediaWriteProtected, StatusOf(await HandleAsync(c, Head(Command.Create, tree), CreateSeg("newdir", 0, Disposition.OpenIf, CreateOptions.DirectoryFile))), "mkdir");
        Assert.AreEqual(Status.MediaWriteProtected, StatusOf(await HandleAsync(c, Head(Command.Create, tree), CreateSeg("f.txt", 0, Disposition.Open, CreateOptions.DeleteOnClose))), "delete on close");

        var fileId = await OpenAsync(c, tree, "f.txt", 0, Disposition.OpenIf);
        var read = new byte[48];
        Wire.Put32(read, 4, 5);
        fileId.CopyTo(read, 16);
        var (status, reply) = await c.ReadAsync(read, None);
        using (reply)
        {
            Assert.AreEqual(Status.Success, status);
            Assert.AreEqual("hello", Encoding.ASCII.GetString(reply.Body.Slice(16, 5)));
        }

        var write = new byte[52];
        Wire.Put16(write, 2, Smb2.HeaderSize + 48);
        Wire.Put32(write, 4, 4);
        fileId.CopyTo(write, 16);
        (status, reply) = await c.WriteAsync(write, None);
        reply.Dispose();
        Assert.AreEqual(Status.MediaWriteProtected, status, "write");

        (status, reply) = await c.SetInfoAsync(SmbTestClient.SetInfoBody(fileId, FileInfoClass.Rename, SmbTestClient.RenameInfo("moved.txt", false)), None);
        reply.Dispose();
        Assert.AreEqual(Status.MediaWriteProtected, status, "rename");

        (status, reply) = await c.SetInfoAsync(SmbTestClient.SetInfoBody(fileId, FileInfoClass.Disposition, [1]), None);
        reply.Dispose();
        Assert.AreEqual(Status.MediaWriteProtected, status, "delete disposition");

        (status, reply) = await c.QueryInfoAsync(SmbTestClient.QueryInfoBody(fileId, InfoType.FileSystem, FsInfoClass.Attribute), None);
        using (reply)
        {
            Assert.AreEqual(Status.Success, status);
            Assert.AreNotEqual(0u, Wire.U32(reply.Body, 8) & 0x00080000, "FILE_READ_ONLY_VOLUME");
        }

        Assert.HasCount(1, Directory.EnumerateFileSystemEntries(dir).ToList(), "nothing reached the folder");
    }

    /// <summary>
    /// An open that reaches no data (Windows' client may still have a file in a folder open for its attributes as it renames
    /// the folder) opens nothing in the file system, whose handle would keep a Windows volume from renaming the folder: it
    /// answers for the file's attributes, renames and deletes it by name, and opens the file only if it reads.
    /// </summary>
    [TestMethod]
    public async Task AnOpenWithoutDataAccessOpensNothingInTheFileSystem()
    {
        var dir = Dir();
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        await File.WriteAllTextAsync(Path.Combine(dir, "sub", "f.txt"), "hello");
        var counting = new CountingFileSystem(new LocalFileSystem(dir));
        var server = Track(SmbServer.Start(new SmbServerOptions { EndPoint = new(System.Net.IPAddress.Loopback, 0) }, [new SmbShare("data", counting)]));
        var c = Connection(server);
        var tree = TreeIdOf(await HandleAsync(c, Head(Command.TreeConnect), TreeConnectSeg("data")));
        const uint readAttributes = 0x80;
        var fileId = await OpenAsync(c, tree, "sub/f.txt", Access.Delete | readAttributes, Disposition.Open);
        Assert.AreEqual(0, counting.Opens, "nothing opened in the file system");

        var (status, reply) = await c.QueryInfoAsync(SmbTestClient.QueryInfoBody(fileId, InfoType.File, FileInfoClass.Standard), None);
        using (reply)
        {
            Assert.AreEqual(Status.Success, status);
            Assert.AreEqual(5UL, Wire.U64(reply.Body, 8 + 8), "EndOfFile");
        }

        // the folder renamed while the open is held: what a Windows volume refused with a file in it open
        var folder = await OpenAsync(c, tree, "sub", Access.Delete | readAttributes, Disposition.Open, CreateOptions.DirectoryFile);
        (status, reply) = await c.SetInfoAsync(SmbTestClient.SetInfoBody(folder, FileInfoClass.Rename, SmbTestClient.RenameInfo("moved", false)), None);
        reply.Dispose();
        Assert.AreEqual(Status.Success, status, "the folder's rename");
        Assert.IsTrue(File.Exists(Path.Combine(dir, "moved", "f.txt")));

        // a client that asked for no data access and reads all the same (some do) gets the data: the file opened now
        var reading = await OpenAsync(c, tree, "moved/f.txt", readAttributes, Disposition.Open);
        var read = new byte[48];
        Wire.Put32(read, 4, 5);
        reading.CopyTo(read, 16);
        (status, reply) = await c.ReadAsync(read, None);
        using (reply)
        {
            Assert.AreEqual(Status.Success, status);
            Assert.AreEqual("hello", Encoding.ASCII.GetString(reply.Body.Slice(16, 5)));
        }

        Assert.AreEqual(1, counting.Opens, "opened at the read");
        (status, reply) = await c.CloseAsync(SmbTestClient.CloseBody(reading), None);
        reply.Dispose();

        var renamed = await OpenAsync(c, tree, "moved/f.txt", Access.Delete | readAttributes, Disposition.Open);
        (status, reply) = await c.SetInfoAsync(SmbTestClient.SetInfoBody(renamed, FileInfoClass.Rename, SmbTestClient.RenameInfo("moved/g.txt", false)), None);
        reply.Dispose();
        Assert.AreEqual(Status.Success, status, "the file's rename by name");
        (status, reply) = await c.SetInfoAsync(SmbTestClient.SetInfoBody(renamed, FileInfoClass.Disposition, [1]), None);
        reply.Dispose();
        Assert.AreEqual(Status.Success, status);
        (status, reply) = await c.CloseAsync(SmbTestClient.CloseBody(renamed), None);
        reply.Dispose();
        Assert.AreEqual(Status.Success, status);
        Assert.IsFalse(File.Exists(Path.Combine(dir, "moved", "g.txt")), "deleted on close");
        Assert.AreEqual(1, counting.Opens, "nothing more opened in the file system");
    }

    /// <summary>A file system that doesn't know its space shows a large volume, not an empty one, which a client takes for full.</summary>
    [TestMethod]
    public async Task UnknownSpaceIsALargeVolumeNotAFullOne()
    {
        var dir = Dir();
        var server = Track(SmbServer.Start(new SmbServerOptions { EndPoint = new(System.Net.IPAddress.Loopback, 0) }, [new SmbShare("data", new CountingFileSystem(new LocalFileSystem(dir)) { KnowsSpace = false })]));
        var c = Connection(server);
        var tree = TreeIdOf(await HandleAsync(c, Head(Command.TreeConnect), TreeConnectSeg("data")));
        var root = await OpenAsync(c, tree, "", 0, Disposition.Open, CreateOptions.DirectoryFile);
        var (status, reply) = await c.QueryInfoAsync(SmbTestClient.QueryInfoBody(root, InfoType.FileSystem, FsInfoClass.FullSize), None);
        using (reply)
        {
            Assert.AreEqual(Status.Success, status);
            var unit = (long)Wire.U32(reply.Body, 8 + 24) * Wire.U32(reply.Body, 8 + 28);
            Assert.AreEqual((1L << 40) / unit, (long)Wire.U64(reply.Body, 8), "TotalAllocationUnits");
            Assert.AreEqual((1L << 39) / unit, (long)Wire.U64(reply.Body, 8 + 8), "CallerAvailableAllocationUnits");
        }
    }

    /// <summary>A file system that counts the calls made of it.</summary>
    private sealed class CountingFileSystem(ISmbFileSystem inner) : ISmbFileSystem
    {
        private int _lists;
        private int _stats;
        private int _opens;

        public int Lists => _lists;

        public int Stats => _stats;

        public bool CaseInsensitive => inner.CaseInsensitive;

        public ValueTask<SmbEntry?> StatAsync(string path, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _stats);
            return inner.StatAsync(path, cancellationToken);
        }

        public async IAsyncEnumerable<SmbEntry> ListAsync(string path, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _lists);
            await foreach (var entry in inner.ListAsync(path, cancellationToken))
            {
                yield return entry;
            }
        }

        public int Opens => _opens;

        public ValueTask<ISmbFile> OpenAsync(string path, SmbOpenMode mode, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _opens);
            return inner.OpenAsync(path, mode, cancellationToken);
        }

        public ValueTask CreateDirectoryAsync(string path, CancellationToken cancellationToken) => inner.CreateDirectoryAsync(path, cancellationToken);

        public ValueTask DeleteAsync(string path, CancellationToken cancellationToken) => inner.DeleteAsync(path, cancellationToken);

        public ValueTask RenameAsync(string fromPath, string toPath, bool replace, CancellationToken cancellationToken) => inner.RenameAsync(fromPath, toPath, replace, cancellationToken);

        public ValueTask SetTimesAsync(string path, DateTimeOffset? created, DateTimeOffset? modified, CancellationToken cancellationToken) => inner.SetTimesAsync(path, created, modified, cancellationToken);

        /// <summary>False: the file system doesn't know its space.</summary>
        public bool KnowsSpace { get; init; } = true;

        public ValueTask<SmbSpace?> GetSpaceAsync(CancellationToken cancellationToken) => KnowsSpace ? inner.GetSpaceAsync(cancellationToken) : ValueTask.FromResult<SmbSpace?>(null);
    }
}
