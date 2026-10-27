using System.Net;
using System.Text;
using LibSmb.Protocol;

namespace LibSmb.Tests;

/// <summary>What the handler-level tests share: a server over temporary folders, a connection with no socket, requests built by hand.</summary>
internal static class TestSupport
{
    public static readonly CancellationToken None = CancellationToken.None;

    public static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "libsmb-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void Remove(string dir)
    {
        try
        {
            Directory.Delete(dir, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>A server on a free loopback port whose first share, "data", is <paramref name="dir"/>.</summary>
    public static SmbServer Server(string dir, string? user = null, string? password = null, params SmbShare[] extra)
        => SmbServer.Start(new SmbServerOptions { EndPoint = new IPEndPoint(IPAddress.Loopback, 0), User = user, Password = password }, [new SmbShare("data", new LocalFileSystem(dir)), .. extra]);

    public static SmbConnection Connection(SmbServer server) => new(server, null);

    public static Header Head(Command command, uint treeId = 0, ulong sessionId = 0, HeaderFlags flags = HeaderFlags.None)
        => new(0, command, 0, flags, 0, 0, treeId, sessionId);

    /// <summary>One command handled as the dispatcher handles it: the reply PDU (header and body), copied out.</summary>
    public static async Task<byte[]> HandleAsync(SmbConnection connection, Header header, byte[] pdu, SmbConnection.Chain? chain = null)
    {
        var (reply, _) = await connection.HandleCommandAsync(header, pdu, chain ?? new SmbConnection.Chain(), None);
        using (reply)
        {
            return reply!.Pdu.ToArray();
        }
    }

    public static uint StatusOf(byte[] pdu) => Wire.U32(pdu, 8);

    public static uint TreeIdOf(byte[] pdu) => Wire.U32(pdu, 36);

    /// <summary>A request PDU: a zeroed 64-byte header and the body.</summary>
    public static byte[] Seg(byte[] body)
    {
        var seg = new byte[Smb2.HeaderSize + body.Length];
        Smb2.Magic.CopyTo(seg);
        body.CopyTo(seg, Smb2.HeaderSize);
        return seg;
    }

    public static byte[] TreeConnectSeg(string share)
    {
        var path = Encoding.Unicode.GetBytes(@"\\host\" + share);
        var body = new byte[8 + path.Length];
        Wire.Put16(body, 0, 9);
        Wire.Put16(body, 4, Smb2.HeaderSize + 8);
        Wire.Put16(body, 6, (ushort)path.Length);
        path.CopyTo(body, 8);
        return Seg(body);
    }

    public static byte[] CreateSeg(string name, uint access = 0, uint disposition = Disposition.Open, uint options = 0)
        => Seg(SmbTestClient.CreateBody(name, access, disposition, options));

    public static byte[] NegotiateBody(params ushort[] dialects)
    {
        var body = new byte[36 + dialects.Length * 2];
        Wire.Put16(body, 0, 36);
        Wire.Put16(body, 2, (ushort)dialects.Length);
        for (var i = 0; i < dialects.Length; i++)
        {
            Wire.Put16(body, 36 + i * 2, dialects[i]);
        }

        return body;
    }

    /// <summary>Connects a tree to a share; its TreeId.</summary>
    public static async Task<uint> TreeConnectAsync(SmbConnection connection, string share = "data")
    {
        var reply = await HandleAsync(connection, Head(Command.TreeConnect), TreeConnectSeg(share));
        Assert.AreEqual(Status.Success, StatusOf(reply), $"tree connect to {share}");
        return TreeIdOf(reply);
    }

    /// <summary>Opens a name on a tree; its FileId.</summary>
    public static async Task<byte[]> OpenAsync(SmbConnection connection, uint treeId, string name, uint access = 0, uint disposition = Disposition.Open, uint options = 0)
    {
        var reply = await HandleAsync(connection, Head(Command.Create, treeId), CreateSeg(name, access, disposition, options));
        Assert.AreEqual(Status.Success, StatusOf(reply), $"open {name}: 0x{StatusOf(reply):X8}");
        return reply.AsSpan(Smb2.HeaderSize + 64, 16).ToArray();
    }

    /// <summary>A directory open put straight into the connection's table, as the Go tests' dirHandle does.</summary>
    public static byte[] DirHandle(SmbConnection connection, SmbServer server, string path)
    {
        var share = server.Shares.Single(s => s.Name == "data");
        var id = connection.AddOpen(id => new OpenFile(id, share, path, null, isDirectory: true, canWrite: true));
        var bytes = new byte[16];
        id.Write(bytes);
        return bytes;
    }

    public static byte[] QueryDirectoryBody(byte[] fileId, byte infoClass, byte flags = 0, string pattern = "", int outputLength = 1 << 16)
    {
        var p = Encoding.Unicode.GetBytes(pattern);
        var body = new byte[32 + p.Length];
        body[2] = infoClass;
        body[3] = flags;
        fileId.CopyTo(body, 8);
        if (p.Length > 0)
        {
            Wire.Put16(body, 24, Smb2.HeaderSize + 32);
            Wire.Put16(body, 26, (ushort)p.Length);
        }

        Wire.Put32(body, 28, (uint)outputLength);
        p.CopyTo(body, 32);
        return body;
    }

    /// <summary>The entries of a QUERY_DIRECTORY reply's info buffer, as (offset, entry) pairs.</summary>
    public static IEnumerable<(int Offset, byte[] Entry)> Entries(byte[] info)
    {
        for (var at = 0; ;)
        {
            var next = (int)Wire.U32(info, at);
            var length = next == 0 ? info.Length - at : next;
            yield return (at, info.AsSpan(at, length).ToArray());
            if (next == 0)
            {
                yield break;
            }

            at += next;
        }
    }

    public static string NameAt(byte[] entry, int nameOffset) => Encoding.Unicode.GetString(entry, nameOffset, (int)Wire.U32(entry, 60));
}
