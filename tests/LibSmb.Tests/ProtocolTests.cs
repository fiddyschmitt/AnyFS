using System.Text;
using LibSmb.Protocol;
using LibSmb.Security;
using static LibSmb.Tests.TestSupport;

namespace LibSmb.Tests;

/// <summary>The commands handled one by one, as portable-smb-server's Go tests have them (internal/smb/smb_test.go), ported.</summary>
[TestClass]
public class ProtocolTests
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

    private SmbServer NewServer(string dir, string? user = null, string? password = null, params SmbShare[] extra)
    {
        var server = Server(dir, user, password, extra);
        _servers.Add(server);
        return server;
    }

    /// <summary>NEGOTIATE fails when the client offers no dialect we speak, rather than forcing an unasked-for 2.0.2.</summary>
    [TestMethod]
    public async Task NegotiateWithNoDialectInCommonFails()
    {
        var c = Connection(NewServer(Dir()));
        var reply = await HandleAsync(c, Head(Command.Negotiate), Seg(NegotiateBody(Dialect.Smb311)));
        Assert.AreEqual(Status.NotSupported, StatusOf(reply), "3.1.1 only");

        reply = await HandleAsync(c, Head(Command.Negotiate), Seg(NegotiateBody(Dialect.Smb202, Dialect.Smb302)));
        Assert.AreEqual(Status.Success, StatusOf(reply));
        Assert.AreEqual(Dialect.Smb302, c.Dialect, "the highest offered");
        Assert.AreEqual(Dialect.Smb302, Wire.U16(reply, Smb2.HeaderSize + 4));
    }

    /// <summary>TREE_CONNECT routes to the right share (names matched in any case) and a CREATE on each tree opens that share's file.</summary>
    [TestMethod]
    public async Task TreeConnectRoutesToTheShare()
    {
        var dir1 = Dir();
        var dir2 = Dir();
        await File.WriteAllTextAsync(Path.Combine(dir1, "f.txt"), "one");
        await File.WriteAllTextAsync(Path.Combine(dir2, "f.txt"), "two");
        var c = Connection(NewServer(dir1, extra: new SmbShare("second", new LocalFileSystem(dir2))));

        async Task<string> ReadVia(string share)
        {
            var tree = await TreeConnectAsync(c, share);
            var fileId = await OpenAsync(c, tree, "f.txt");
            var body = new byte[48];
            Wire.Put32(body, 4, 16);
            fileId.CopyTo(body, 16);
            var (status, reply) = await c.ReadAsync(body, None);
            using (reply)
            {
                Assert.AreEqual(Status.Success, status);
                return Encoding.ASCII.GetString(reply.Body.Slice(16, (int)Wire.U32(reply.Body, 4)));
            }
        }

        Assert.AreEqual("one", await ReadVia("data"));
        Assert.AreEqual("two", await ReadVia("SECOND"));
        Assert.AreEqual(Status.BadNetworkName, StatusOf(await HandleAsync(c, Head(Command.TreeConnect), TreeConnectSeg("nope"))));
        Assert.AreEqual(Status.NetworkNameDeleted, StatusOf(await HandleAsync(c, Head(Command.Create, 9999), CreateSeg("f.txt"))), "a tree never connected");
    }

    [TestMethod]
    public void DuplicateShareNamesAreRefused()
    {
        var fs = new LocalFileSystem(Dir());
        Assert.ThrowsExactly<InvalidOperationException>(() => NewServer(Dir(), extra: new SmbShare("DATA", fs)));
        Assert.ThrowsExactly<ArgumentException>(() => new SmbShare("ipc$", fs));
    }

    /// <summary>A path's file id is stable, and differs from path to path and from share to share.</summary>
    [TestMethod]
    public void PathFileIdsAreStableAndDistinct()
    {
        Assert.AreEqual(OpenFile.PathFileId("s", "a/b"), OpenFile.PathFileId("s", "a/b"));
        Assert.AreNotEqual(OpenFile.PathFileId("s", "a/b"), OpenFile.PathFileId("s", "a/c"));
        Assert.AreNotEqual(OpenFile.PathFileId("s1", "a/b"), OpenFile.PathFileId("s2", "a/b"));

        // the same ids as portable-smb-server's, so a client's cache does not see every file change
        Span<byte> hash = stackalloc byte[16];
        System.Security.Cryptography.MD5.HashData("data\0a/b"u8, hash);
        Assert.AreEqual(Wire.U64(hash, 0), OpenFile.PathFileId("data", "a/b"));
    }

    /// <summary>
    /// SMB2_RETURN_SINGLE_ENTRY (Windows' FindFirstFile) gets exactly one entry, and the calls after miss none.
    /// </summary>
    [TestMethod]
    public async Task ReturnSingleEntryReturnsOneAndLosesNone()
    {
        var dir = Dir();
        for (var i = 0; i < 5; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(dir, $"f{i}.txt"), "");
        }

        var server = NewServer(dir);
        var c = Connection(server);
        var fileId = DirHandle(c, server, "");
        var open = c.FindOpen(FileId.Read(fileId))!;

        var (status, reply) = await c.QueryDirectoryAsync(QueryDirectoryBody(fileId, FileInfoClass.Directory, QueryDirectoryFlags.ReturnSingleEntry), None);
        reply.Dispose();
        Assert.AreEqual(Status.Success, status);
        Assert.AreEqual(1, open.DirectoryPosition);

        (status, reply) = await c.QueryDirectoryAsync(QueryDirectoryBody(fileId, FileInfoClass.Directory), None);
        reply.Dispose();
        Assert.AreEqual(Status.Success, status);
        Assert.AreEqual(5, open.DirectoryPosition);

        (status, reply) = await c.QueryDirectoryAsync(QueryDirectoryBody(fileId, FileInfoClass.Directory), None);
        reply.Dispose();
        Assert.AreEqual(Status.NoMoreFiles, status);
    }

    /// <summary>A directory entry's FileId is the stable path-derived id.</summary>
    [TestMethod]
    public async Task DirectoryEntriesCarryThePathFileId()
    {
        var dir = Dir();
        await File.WriteAllTextAsync(Path.Combine(dir, "file.tmp"), "");
        var server = NewServer(dir);
        var c = Connection(server);
        var fileId = DirHandle(c, server, "");
        var (status, reply) = await c.QueryDirectoryAsync(QueryDirectoryBody(fileId, FileInfoClass.IdBothDirectory), None);
        using (reply)
        {
            Assert.AreEqual(Status.Success, status);
            var info = reply.Body[8..];
            Assert.IsGreaterThanOrEqualTo(104, info.Length);
            Assert.AreEqual(OpenFile.PathFileId("data", "file.tmp"), Wire.U64(info, 96));
        }
    }

    /// <summary>The search pattern is honoured: a lookup by name finds that entry (not the first), and the wildcards match as Windows' do.</summary>
    [TestMethod]
    public async Task TheSearchPatternIsHonoured()
    {
        var dir = Dir();
        foreach (var name in new[] { "alpha", "bravo", "charlie" })
        {
            Directory.CreateDirectory(Path.Combine(dir, name));
        }

        var server = NewServer(dir);
        var c = Connection(server);
        var fileId = DirHandle(c, server, "");
        var (status, reply) = await c.QueryDirectoryAsync(QueryDirectoryBody(fileId, FileInfoClass.IdBothDirectory, QueryDirectoryFlags.RestartScans | QueryDirectoryFlags.ReturnSingleEntry, "bravo"), None);
        using (reply)
        {
            Assert.AreEqual(Status.Success, status);
            var info = reply.Body[8..].ToArray();
            Assert.AreEqual("bravo", NameAt(info, 104));
        }

        foreach (var (pattern, name, insensitive, matches) in new[]
        {
            ("Users", "users", true, true),
            ("Users", "users", false, false),
            ("Users", "Users", false, true),
            ("*.txt", "a.TXT", true, true),
            ("*.txt", "a.TXT", false, false),
            ("Users", "Default", true, false),
            ("f?.tx<", "f1.txt", true, true), // the DOS wildcards < and > are * and ?
            ("*", "anything", false, true),
            ("", "anything", false, true),
            ("a*b*c", "aXbYc", false, true),
            ("a*b*c", "aXbY", false, false),
        })
        {
            Assert.AreEqual(matches, SmbPattern.Matches(pattern, name, insensitive), $"'{pattern}' against '{name}', case-insensitive {insensitive}");
        }
    }

    /// <summary>
    /// A directory that cannot be listed is answered as empty, not as a failure: a generic failure makes the Windows shell's
    /// copy give up on a whole recursive copy.
    /// </summary>
    [TestMethod]
    public async Task ADirectoryThatCannotBeListedIsEmpty()
    {
        var server = NewServer(Dir());
        var c = Connection(server);
        var fileId = DirHandle(c, server, "nope-does-not-exist");
        var (status, reply) = await c.QueryDirectoryAsync(QueryDirectoryBody(fileId, FileInfoClass.IdBothDirectory), None);
        reply.Dispose();
        Assert.AreEqual(Status.NoMoreFiles, status);
    }

    /// <summary>
    /// With an account, a command on a session that never signed in is refused; one that did passes; a guest server gates
    /// nothing.
    /// </summary>
    [TestMethod]
    public async Task CommandsNeedASignedInSessionWhenThereIsAnAccount()
    {
        var c = Connection(NewServer(Dir(), "alice", "s3cret"));
        Assert.AreEqual(Status.UserSessionDeleted, StatusOf(await HandleAsync(c, Head(Command.TreeConnect, sessionId: 999), TreeConnectSeg("data"))));
        c.AddSessionForTests(999);
        Assert.AreEqual(Status.Success, StatusOf(await HandleAsync(c, Head(Command.TreeConnect, sessionId: 999), TreeConnectSeg("data"))));

        var guest = Connection(NewServer(Dir()));
        Assert.AreEqual(Status.Success, StatusOf(await HandleAsync(guest, Head(Command.TreeConnect, sessionId: 42), TreeConnectSeg("data"))), "a guest server gates nothing");
    }

    /// <summary>A signed request from a session with a key passes only with the right signature.</summary>
    [TestMethod]
    public async Task ASignedRequestNeedsTheRightSignature()
    {
        var c = Connection(NewServer(Dir()));
        c.Dialect = Dialect.Smb202;
        var signer = c.AddSessionForTests(7, new byte[16])!;
        var seg = new byte[Smb2.HeaderSize + 4];
        Smb2.Magic.CopyTo(seg);
        Wire.Put16(seg, 4, Smb2.HeaderSize);
        Wire.Put16(seg, 12, (ushort)Command.Echo);
        Wire.Put64(seg, 40, 7);
        Wire.Put16(seg, Smb2.HeaderSize, 4);
        signer.Sign(seg);
        Assert.IsTrue(Header.TryParse(seg, out var header));
        Assert.AreEqual(Status.Success, StatusOf(await HandleAsync(c, header, seg)), "rightly signed");
        seg[48] ^= 0xFF;
        Assert.AreEqual(Status.AccessDenied, StatusOf(await HandleAsync(c, header, seg)), "a signature tampered with");
    }

    /// <summary>
    /// A signed related command (Linux and macOS compound CREATE, QUERY_INFO, CLOSE) is checked as the client signed it, with
    /// the all-0xFF FileId placeholder in place, and only then given the CREATE's handle. Checked after the substitution,
    /// every such command was refused, and a mount with signing required failed.
    /// </summary>
    [TestMethod]
    public async Task ARelatedSignedCommandIsCheckedBeforeItsFileIdIsReplaced()
    {
        var server = NewServer(Dir());
        var c = Connection(server);
        c.Dialect = Dialect.Smb302;
        var signer = c.AddSessionForTests(7, new byte[16])!;
        var fileId = DirHandle(c, server, "");
        var chain = new SmbConnection.Chain { FileId = FileId.Read(fileId), SessionId = 7 };

        var seg = new byte[Smb2.HeaderSize + 24];
        Smb2.Magic.CopyTo(seg);
        Wire.Put16(seg, 4, Smb2.HeaderSize);
        Wire.Put16(seg, 12, (ushort)Command.Close);
        Wire.Put32(seg, 16, (uint)(HeaderFlags.Signed | HeaderFlags.RelatedOperations));
        Wire.Put16(seg, Smb2.HeaderSize, 24);
        seg.AsSpan(Smb2.HeaderSize + 8, 16).Fill(0xFF);
        signer.Sign(seg);
        Assert.IsTrue(Header.TryParse(seg, out var header));
        Assert.AreEqual(Status.Success, StatusOf(await HandleAsync(c, header, seg, chain)));
        Assert.IsNull(c.FindOpen(FileId.Read(fileId)), "the CLOSE reached the CREATE's handle");
    }

    /// <summary>The AES-CMAC path (3.x) signs and checks, and a change is caught.</summary>
    [TestMethod]
    public void Smb3SigningRoundTrips()
    {
        using var signer = new Signer([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16], Dialect.Smb302);
        var message = new byte[Smb2.HeaderSize + 8];
        Smb2.Magic.CopyTo(message);
        signer.Sign(message);
        Assert.IsTrue(signer.Verify(message));
        message[70] ^= 1;
        Assert.IsFalse(signer.Verify(message));
    }

    /// <summary>A READ longer than MaxReadSize is refused rather than allocated; a zero-length READ succeeds; reads read.</summary>
    [TestMethod]
    public async Task ReadLengthsAreBounded()
    {
        var dir = Dir();
        await File.WriteAllTextAsync(Path.Combine(dir, "f.txt"), "hello");
        var c = Connection(NewServer(dir));
        var tree = await TreeConnectAsync(c);
        var fileId = await OpenAsync(c, tree, "f.txt");

        byte[] ReadBody(uint length)
        {
            var body = new byte[48];
            Wire.Put32(body, 4, length);
            fileId.CopyTo(body, 16);
            return body;
        }

        var (status, reply) = await c.ReadAsync(ReadBody(Smb2.MaxIoSize + 1), None);
        reply.Dispose();
        Assert.AreEqual(Status.InvalidParameter, status);

        (status, reply) = await c.ReadAsync(ReadBody(5), None);
        using (reply)
        {
            Assert.AreEqual(Status.Success, status);
            Assert.AreEqual("hello", Encoding.ASCII.GetString(reply.Body.Slice(16, 5)));
            Assert.AreEqual(Smb2.HeaderSize + 16, reply.Body[2], "DataOffset");
        }

        (status, reply) = await c.ReadAsync(ReadBody(0), None);
        using (reply)
        {
            Assert.AreEqual(Status.Success, status, "a zero-length READ");
            Assert.AreEqual(0u, Wire.U32(reply.Body, 4));
        }
    }

    /// <summary>A failed close of the file (a buffered write that did not land) is told to the client, not passed off as success.</summary>
    [TestMethod]
    public async Task AFailedCloseIsReported()
    {
        var server = NewServer(Dir());
        var c = Connection(server);
        var share = server.Shares.Single();
        var id = c.AddOpen(id => new OpenFile(id, share, "f.txt", new FailingClose(), isDirectory: false, canWrite: true));
        var body = new byte[24];
        id.Write(body.AsSpan(8));
        var (status, reply) = await c.CloseAsync(body, None);
        reply.Dispose();
        Assert.AreNotEqual(Status.Success, status);
    }

    /// <summary>A rename onto an existing name is a collision unless ReplaceIfExists is set.</summary>
    [TestMethod]
    public async Task ARenameOntoAnExistingNameNeedsReplaceIfExists()
    {
        var dir = Dir();
        await File.WriteAllTextAsync(Path.Combine(dir, "src.txt"), "s");
        await File.WriteAllTextAsync(Path.Combine(dir, "dst.txt"), "d");
        var c = Connection(NewServer(dir));
        var tree = await TreeConnectAsync(c);
        var fileId = await OpenAsync(c, tree, "src.txt", Access.Delete);

        var (status, reply) = await c.SetInfoAsync(SmbTestClient.SetInfoBody(fileId, FileInfoClass.Rename, SmbTestClient.RenameInfo("dst.txt", false)), None);
        reply.Dispose();
        Assert.AreEqual(Status.ObjectNameCollision, status);

        (status, reply) = await c.SetInfoAsync(SmbTestClient.SetInfoBody(fileId, FileInfoClass.Rename, SmbTestClient.RenameInfo("moved.txt", false)), None);
        reply.Dispose();
        Assert.AreEqual(Status.Success, status);
        Assert.IsTrue(File.Exists(Path.Combine(dir, "moved.txt")));

        (status, reply) = await c.SetInfoAsync(SmbTestClient.SetInfoBody(fileId, FileInfoClass.Rename, SmbTestClient.RenameInfo("dst.txt", true)), None);
        reply.Dispose();
        Assert.AreEqual(Status.Success, status, "with ReplaceIfExists");
        Assert.AreEqual("s", await File.ReadAllTextAsync(Path.Combine(dir, "dst.txt")));
    }

    /// <summary>A name opened on IPC$ is refused, not taken for a file (a real file named like a pipe, srvsvc, must not open).</summary>
    [TestMethod]
    public async Task ACreateOnIpcIsRefused()
    {
        var dir = Dir();
        await File.WriteAllTextAsync(Path.Combine(dir, "srvsvc"), "data");
        var c = Connection(NewServer(dir));
        var disk = await TreeConnectAsync(c);
        Assert.AreEqual(Status.Success, StatusOf(await HandleAsync(c, Head(Command.Create, disk), CreateSeg("srvsvc"))));
        var ipc = await TreeConnectAsync(c, "IPC$");
        Assert.AreEqual(Status.ObjectNameNotFound, StatusOf(await HandleAsync(c, Head(Command.Create, ipc), CreateSeg("srvsvc"))));
    }

    /// <summary>Names with .. cannot leave the share.</summary>
    [TestMethod]
    public async Task DotDotStaysInTheShare()
    {
        var parent = Dir();
        var dir = Path.Combine(parent, "shared");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(parent, "secret.txt"), "secret");
        var c = Connection(NewServer(dir));
        var tree = await TreeConnectAsync(c);
        foreach (var name in new[] { @"..\secret.txt", @"foo\..\..\secret.txt", @"\..\secret.txt", "../secret.txt" })
        {
            Assert.AreNotEqual(Status.Success, StatusOf(await HandleAsync(c, Head(Command.Create, tree), CreateSeg(name))), name);
        }

        Assert.AreEqual("secret.txt", SmbPath.FromSmbName(@"..\..\secret.txt"), "anchored at the root");
        Assert.AreEqual("a/b", SmbPath.FromSmbName(@"\a\.\c\..\b\"));
    }

    /// <summary>A connection cannot hold more than the cap of opens (a client that never closes would use up the backend's handles).</summary>
    [TestMethod]
    public async Task OpensAreCapped()
    {
        var dir = Dir();
        await File.WriteAllTextAsync(Path.Combine(dir, "f.txt"), "x");
        var server = NewServer(dir);
        var c = Connection(server);
        var tree = await TreeConnectAsync(c);
        for (var i = 0; i < SmbConnection.MaxOpenFiles; i++)
        {
            DirHandle(c, server, "");
        }

        Assert.AreEqual(Status.InsufficientResources, StatusOf(await HandleAsync(c, Head(Command.Create, tree), CreateSeg("f.txt"))));
    }

    [TestMethod]
    public void AUserNeedsAPassword()
    {
        Assert.ThrowsExactly<ArgumentException>(() => NewServer(Dir(), "alice", ""));
        NewServer(Dir(), "alice", "s3cret");
        NewServer(Dir());
    }

    /// <summary>A few edge cases' statuses.</summary>
    [TestMethod]
    public async Task EdgeCases()
    {
        var dir = Dir();
        await File.WriteAllTextAsync(Path.Combine(dir, "f.txt"), "hello");
        var c = Connection(NewServer(dir));
        var tree = await TreeConnectAsync(c);

        // FILE_DIRECTORY_FILE on a file
        Assert.AreEqual(Status.NotADirectory, StatusOf(await HandleAsync(c, Head(Command.Create, tree), CreateSeg("f.txt", options: CreateOptions.DirectoryFile))));

        // FILE_NON_DIRECTORY_FILE on a directory
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        Assert.AreEqual(Status.FileIsADirectory, StatusOf(await HandleAsync(c, Head(Command.Create, tree), CreateSeg("sub", options: CreateOptions.NonDirectoryFile))));

        // FILE_CREATE on an existing name
        Assert.AreEqual(Status.ObjectNameCollision, StatusOf(await HandleAsync(c, Head(Command.Create, tree), CreateSeg("f.txt", disposition: Disposition.Create))));

        // FILE_OPEN of nothing
        Assert.AreEqual(Status.ObjectNameNotFound, StatusOf(await HandleAsync(c, Head(Command.Create, tree), CreateSeg("missing.txt"))));

        // an unknown SET_INFO class
        var fileId = await OpenAsync(c, tree, "f.txt");
        var (status, reply) = await c.SetInfoAsync(SmbTestClient.SetInfoBody(fileId, 0xEE, []), None);
        reply.Dispose();
        Assert.AreEqual(Status.InvalidInfoClass, status);

        // a WRITE on an open without write access
        var write = new byte[52];
        Wire.Put16(write, 2, Smb2.HeaderSize + 48);
        Wire.Put32(write, 4, 4);
        fileId.CopyTo(write, 16);
        (status, reply) = await c.WriteAsync(write, None);
        reply.Dispose();
        Assert.AreEqual(Status.AccessDenied, status);

        // ECHO and LOCK
        Assert.AreEqual(Status.Success, StatusOf(await HandleAsync(c, Head(Command.Echo), Seg(new byte[4]))));
        Assert.AreEqual(Status.Success, StatusOf(await HandleAsync(c, Head(Command.Lock), Seg(new byte[48]))));

        // what is not served
        Assert.AreEqual(Status.NotSupported, StatusOf(await HandleAsync(c, Head(Command.ChangeNotify), Seg(new byte[32]))));
    }

    /// <summary>
    /// A QUERY_INFO reply never exceeds the client's OutputBufferLength: too long, it is cut and answered BUFFER_OVERFLOW.
    /// More bytes than were asked for make the Windows redirector reject the response (ERROR_BAD_NET_RESP).
    /// </summary>
    [TestMethod]
    public async Task QueryInfoRespectsTheOutputBufferLength()
    {
        var dir = Dir();
        await File.WriteAllTextAsync(Path.Combine(dir, "f.txt"), "hello");
        var c = Connection(NewServer(dir));
        var tree = await TreeConnectAsync(c);
        var fileId = await OpenAsync(c, tree, "f.txt");

        var (status, reply) = await c.QueryInfoAsync(SmbTestClient.QueryInfoBody(fileId, InfoType.FileSystem, FsInfoClass.Volume, 4096), None);
        int full;
        using (reply)
        {
            Assert.AreEqual(Status.Success, status);
            full = (int)Wire.U32(reply.Body, 4);
            Assert.AreEqual(reply.BodyLength - 8, full);
            Assert.IsGreaterThanOrEqualTo(20, full);
        }

        (status, reply) = await c.QueryInfoAsync(SmbTestClient.QueryInfoBody(fileId, InfoType.FileSystem, FsInfoClass.Volume, full - 2), None);
        using (reply)
        {
            Assert.AreEqual(Status.BufferOverflow, status);
            Assert.AreEqual((uint)(full - 2), Wire.U32(reply.Body, 4));
            Assert.AreEqual(full - 2, reply.BodyLength - 8, "never more than asked for");
        }
    }

    /// <summary>
    /// CLOSE with POSTQUERY_ATTRIB comes back with the flag and the final size and time; without it, zeros; a delete on close
    /// gets the plain reply (no attributes for a vanishing file, which a caching client would take for a live one) and the
    /// file goes.
    /// </summary>
    [TestMethod]
    public async Task CloseReturnsTheFinalAttributesWhenAsked()
    {
        var dir = Dir();
        await File.WriteAllTextAsync(Path.Combine(dir, "f.txt"), "hello world");
        var c = Connection(NewServer(dir));
        var tree = await TreeConnectAsync(c);

        var (status, reply) = await c.CloseAsync(SmbTestClient.CloseBody(await OpenAsync(c, tree, "f.txt"), postQuery: true), None);
        using (reply)
        {
            Assert.AreEqual(Status.Success, status);
            Assert.AreEqual(60, reply.BodyLength);
            Assert.AreEqual(CloseFlags.PostQueryAttributes, Wire.U16(reply.Body, 2));
            Assert.AreEqual(11ul, Wire.U64(reply.Body, 48));
            Assert.AreNotEqual(0ul, Wire.U64(reply.Body, 24), "the write time");
        }

        (status, reply) = await c.CloseAsync(SmbTestClient.CloseBody(await OpenAsync(c, tree, "f.txt")), None);
        using (reply)
        {
            Assert.AreEqual(Status.Success, status);
            Assert.AreEqual(0, Wire.U16(reply.Body, 2));
            Assert.AreEqual(0ul, Wire.U64(reply.Body, 48));
        }

        var deleting = await OpenAsync(c, tree, "f.txt", Access.Delete, options: CreateOptions.DeleteOnClose);
        (status, reply) = await c.CloseAsync(SmbTestClient.CloseBody(deleting, postQuery: true), None);
        using (reply)
        {
            Assert.AreEqual(Status.Success, status);
            Assert.AreEqual(0, Wire.U16(reply.Body, 2), "no attributes for a file deleted on close");
        }

        Assert.IsFalse(File.Exists(Path.Combine(dir, "f.txt")));
    }

    /// <summary>
    /// A directory with anything in it cannot be marked for deletion (STATUS_DIRECTORY_NOT_EMPTY at SET_INFO, which the
    /// Windows client relies on); an empty one can, and goes at CLOSE.
    /// </summary>
    [TestMethod]
    public async Task ANonEmptyDirectoryCannotBeMarkedForDeletion()
    {
        var dir = Dir();
        Directory.CreateDirectory(Path.Combine(dir, "full"));
        await File.WriteAllTextAsync(Path.Combine(dir, "full", "a.txt"), "a");
        Directory.CreateDirectory(Path.Combine(dir, "empty"));
        var c = Connection(NewServer(dir));
        var tree = await TreeConnectAsync(c);

        var full = await OpenAsync(c, tree, "full", Access.Delete, options: CreateOptions.DirectoryFile);
        var (status, reply) = await c.SetInfoAsync(SmbTestClient.SetInfoBody(full, FileInfoClass.Disposition, [1]), None);
        reply.Dispose();
        Assert.AreEqual(Status.DirectoryNotEmpty, status);

        var empty = await OpenAsync(c, tree, "empty", Access.Delete, options: CreateOptions.DirectoryFile);
        (status, reply) = await c.SetInfoAsync(SmbTestClient.SetInfoBody(empty, FileInfoClass.Disposition, [1]), None);
        reply.Dispose();
        Assert.AreEqual(Status.Success, status);
        (status, reply) = await c.CloseAsync(SmbTestClient.CloseBody(empty), None);
        reply.Dispose();
        Assert.AreEqual(Status.Success, status);
        Assert.IsFalse(Directory.Exists(Path.Combine(dir, "empty")));
        Assert.IsTrue(Directory.Exists(Path.Combine(dir, "full")));
    }

    /// <summary>
    /// FileAllocationInformation is accepted and changes nothing (Windows sends it before the end of file when it sets a
    /// length; refused, every truncate from Windows failed); FileEndOfFileInformation truncates and extends.
    /// </summary>
    [TestMethod]
    public async Task AllocationIsAcceptedAndEndOfFileSetsTheLength()
    {
        var dir = Dir();
        await File.WriteAllTextAsync(Path.Combine(dir, "f.txt"), "hello world");
        var c = Connection(NewServer(dir));
        var tree = await TreeConnectAsync(c);
        var fileId = await OpenAsync(c, tree, "f.txt", Access.FileWriteData);

        var allocation = new byte[8];
        Wire.Put64(allocation, 0, 4096);
        var (status, reply) = await c.SetInfoAsync(SmbTestClient.SetInfoBody(fileId, FileInfoClass.Allocation, allocation), None);
        reply.Dispose();
        Assert.AreEqual(Status.Success, status);
        Assert.AreEqual(11, new FileInfo(Path.Combine(dir, "f.txt")).Length);

        var eof = new byte[8];
        Wire.Put64(eof, 0, 5);
        (status, reply) = await c.SetInfoAsync(SmbTestClient.SetInfoBody(fileId, FileInfoClass.EndOfFile, eof), None);
        reply.Dispose();
        Assert.AreEqual(Status.Success, status);
        Wire.Put64(eof, 0, 1 << 20);
        (status, reply) = await c.SetInfoAsync(SmbTestClient.SetInfoBody(fileId, FileInfoClass.EndOfFile, eof), None);
        reply.Dispose();
        Assert.AreEqual(Status.Success, status);
        (status, reply) = await c.CloseAsync(SmbTestClient.CloseBody(fileId), None);
        reply.Dispose();
        var bytes = await File.ReadAllBytesAsync(Path.Combine(dir, "f.txt"));
        Assert.AreEqual(1 << 20, bytes.Length);
        Assert.AreEqual("hello", Encoding.ASCII.GetString(bytes, 0, 5));
        Assert.AreEqual(0, bytes[5]);
    }

    /// <summary>
    /// A failure the file system did not classify takes the status of the I/O error behind it (the disk full, a file another
    /// process holds open), however deep it is wrapped; one with none is STATUS_UNSUCCESSFUL.
    /// </summary>
    [TestMethod]
    public void AnUnclassifiedFailureTakesTheStatusOfItsCause()
    {
        var full = OperatingSystem.IsWindows() ? new IOException("disk full", unchecked((int)0x80070070)) : new IOException("disk full", 28);
        Assert.AreEqual(Status.DiskFull, Errors.StatusOf(new SmbFileSystemException(SmbFileSystemError.Failed, "write failed", new InvalidOperationException("wrapped", full))));
        Assert.AreEqual(Status.Unsuccessful, Errors.StatusOf(new SmbFileSystemException(SmbFileSystemError.Failed, "no cause")));
        Assert.AreEqual(Status.ObjectNameNotFound, Errors.StatusOf(new SmbFileSystemException(SmbFileSystemError.NotFound, "classified", full)), "a classified failure keeps its own status");
    }

    /// <summary>A file read-only itself refuses write opens and delete with ACCESS_DENIED (as Windows does) and shows FILE_ATTRIBUTE_READONLY.</summary>
    [TestMethod]
    public async Task AReadOnlyFileRefusesWritesAndDeletes()
    {
        var dir = Dir();
        var path = Path.Combine(dir, "locked.txt");
        await File.WriteAllTextAsync(path, "x");
        File.SetAttributes(path, System.IO.FileAttributes.ReadOnly);
        try
        {
            var c = Connection(NewServer(dir));
            var tree = await TreeConnectAsync(c);
            Assert.AreEqual(Status.AccessDenied, StatusOf(await HandleAsync(c, Head(Command.Create, tree), CreateSeg("locked.txt", Access.FileWriteData))));
            Assert.AreEqual(Status.AccessDenied, StatusOf(await HandleAsync(c, Head(Command.Create, tree), CreateSeg("locked.txt", Access.Delete, options: CreateOptions.DeleteOnClose))));
            var fileId = await OpenAsync(c, tree, "locked.txt");
            var (status, reply) = await c.QueryInfoAsync(SmbTestClient.QueryInfoBody(fileId, InfoType.File, FileInfoClass.Basic), None);
            using (reply)
            {
                Assert.AreEqual(Status.Success, status);
                Assert.AreEqual(FileAttribute.ReadOnly, Wire.U32(reply.Body, 8 + 32));
            }

            (status, reply) = await c.SetInfoAsync(SmbTestClient.SetInfoBody(fileId, FileInfoClass.Disposition, [1]), None);
            reply.Dispose();
            Assert.AreEqual(Status.AccessDenied, status);
        }
        finally
        {
            File.SetAttributes(path, System.IO.FileAttributes.Normal);
        }
    }

    /// <summary>The validate-negotiate IOCTL is answered with what NEGOTIATE said; DFS referrals are "not found"; others not supported.</summary>
    [TestMethod]
    public async Task IoctlAnswersValidateNegotiateAndRefusesDfs()
    {
        var server = NewServer(Dir());
        var c = Connection(server);
        await HandleAsync(c, Head(Command.Negotiate), Seg(NegotiateBody(Dialect.Smb300)));
        var body = new byte[56];
        Wire.Put32(body, 4, Fsctl.ValidateNegotiateInfo);
        var (status, reply) = c.Ioctl(body);
        using (reply)
        {
            Assert.AreEqual(Status.Success, status);
            var output = reply.Body[48..];
            Assert.IsTrue(output.Slice(4, 16).SequenceEqual(server.ServerGuid));
            Assert.AreEqual(SecurityMode.SigningEnabled, Wire.U16(output, 20));
            Assert.AreEqual(Dialect.Smb300, Wire.U16(output, 22));
        }

        Wire.Put32(body, 4, Fsctl.DfsGetReferrals);
        (status, reply) = c.Ioctl(body);
        reply.Dispose();
        Assert.AreEqual(Status.NotFound, status);
        Wire.Put32(body, 4, 0x00090000);
        (status, reply) = c.Ioctl(body);
        reply.Dispose();
        Assert.AreEqual(Status.NotSupported, status);
    }

    /// <summary>
    /// A related command after a CREATE that failed fails with the CREATE's status ([MS-SMB2] 3.3.5.2.7.2), as Windows and
    /// Samba answer a compound CREATE, QUERY_INFO, CLOSE of a name that is not there.
    /// </summary>
    [TestMethod]
    public async Task ARelatedCommandAfterAFailedCreateFailsTheSameWay()
    {
        var c = Connection(NewServer(Dir()));
        var tree = await TreeConnectAsync(c);
        var chain = new SmbConnection.Chain();
        var create = await HandleAsync(c, Head(Command.Create, tree), CreateSeg("missing.txt"), chain);
        Assert.AreEqual(Status.ObjectNameNotFound, StatusOf(create));
        var close = Seg(SmbTestClient.CloseBody(SmbTestClient.NoFileId));
        Assert.AreEqual(Status.ObjectNameNotFound, StatusOf(await HandleAsync(c, Head(Command.Close, flags: HeaderFlags.RelatedOperations), close, chain)));
    }

    /// <summary>A share removed while a tree is connected to it answers NETWORK_NAME_DELETED, and its opens are closed.</summary>
    [TestMethod]
    public async Task ARemovedShareIsGoneForItsTreesAndOpens()
    {
        var dir = Dir();
        await File.WriteAllTextAsync(Path.Combine(dir, "f.txt"), "x");
        var server = NewServer(dir);
        var c = Connection(server);
        var tree = await TreeConnectAsync(c);
        var fileId = await OpenAsync(c, tree, "f.txt");
        server.Connections.TryAdd(c, 0);
        Assert.IsTrue(await server.RemoveShareAsync("data"));
        Assert.IsNull(c.FindOpen(FileId.Read(fileId)));
        Assert.AreEqual(Status.NetworkNameDeleted, StatusOf(await HandleAsync(c, Head(Command.Create, tree), CreateSeg("f.txt"))));
        Assert.IsFalse(await server.RemoveShareAsync("data"));

        server.AddShare(new SmbShare("data", new LocalFileSystem(dir)));
        Assert.AreEqual(Status.NetworkNameDeleted, StatusOf(await HandleAsync(c, Head(Command.Create, tree), CreateSeg("f.txt"))), "a share added again is a new share: the old tree stays gone");
        var again = await TreeConnectAsync(c);
        await OpenAsync(c, again, "f.txt");
    }

    private sealed class FailingClose : ISmbFile
    {
        public long Length => 0;

        public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken cancellationToken) => new(0);

        public ValueTask WriteAsync(long offset, ReadOnlyMemory<byte> data, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask SetLengthAsync(long length, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask FlushAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => throw new IOException("a simulated flush failure");
    }
}
