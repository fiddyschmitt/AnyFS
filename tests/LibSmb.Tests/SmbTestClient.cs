using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using LibSmb.Protocol;
using LibSmb.Security;

namespace LibSmb.Tests;

/// <summary>A response as the client got it: its header fields and its body.</summary>
internal sealed record SmbResponse(uint Status, Command Command, ulong MessageId, ulong SessionId, uint TreeId, ushort Credits, uint Flags, byte[] Pdu)
{
    public byte[] Body => Pdu[Smb2.HeaderSize..];

    public bool Signed => (Flags & (uint)HeaderFlags.Signed) != 0;

    public SmbResponse EnsureSuccess()
    {
        if (LibSmb.Protocol.Status.IsError(Status))
        {
            throw new InvalidOperationException($"{Command} failed with 0x{this.Status:X8}");
        }

        return this;
    }
}

/// <summary>
/// A small SMB2 client for the tests, over a real TCP connection: NEGOTIATE with chosen dialects, NTLMv2 sign-in (in
/// SPNEGO or bare) with key exchange or a guest one, signing (every request of a signed session, and every response
/// checked), compound chains, and the file commands. It speaks [MS-SMB2] as written; the clients of Windows, macOS and
/// Linux are the end-to-end tests' business.
/// </summary>
internal sealed class SmbTestClient : IAsyncDisposable
{
    public static readonly byte[] NoFileId = [.. Enumerable.Repeat((byte)0xFF, 16)];

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private ulong _messageId;
    private Signer? _signer;

    private SmbTestClient(TcpClient tcp)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
    }

    public ushort Dialect { get; private set; }

    public ulong SessionId { get; private set; }

    public bool Guest { get; private set; }

    /// <summary>Whether requests are signed (on when the session has a key, unless the test turns it off).</summary>
    public bool SignRequests { get; set; } = true;

    /// <summary>The session key the sign-in derived (null for a guest).</summary>
    public byte[]? SessionKey { get; private set; }

    public static async Task<SmbTestClient> ConnectAsync(IPEndPoint server)
    {
        var tcp = new TcpClient(server.AddressFamily) { NoDelay = true };
        await tcp.ConnectAsync(server.Address, server.Port);
        return new SmbTestClient(tcp);
    }

    /// <summary>Connects, negotiates and signs in: with the account, or as a guest when <paramref name="user"/> is null.</summary>
    public static async Task<SmbTestClient> SignInAsync(IPEndPoint server, string? user, string? password, ushort[]? dialects = null, bool spnego = true)
    {
        var client = await ConnectAsync(server);
        await client.NegotiateAsync(dialects ?? [Protocol.Dialect.Smb202, Protocol.Dialect.Smb210, Protocol.Dialect.Smb300, Protocol.Dialect.Smb302]);
        var status = await client.SessionSetupAsync(user, password, spnego);
        if (status != Status.Success)
        {
            await client.DisposeAsync();
            throw new InvalidOperationException($"sign-in failed with 0x{status:X8}");
        }

        return client;
    }

    public async Task<SmbResponse> NegotiateAsync(ushort[] dialects)
    {
        var body = new byte[36 + dialects.Length * 2];
        Wire.Put16(body, 0, 36);
        Wire.Put16(body, 2, (ushort)dialects.Length);
        Wire.Put16(body, 4, SecurityMode.SigningEnabled);
        for (var i = 0; i < dialects.Length; i++)
        {
            Wire.Put16(body, 36 + i * 2, dialects[i]);
        }

        var response = await SendAsync(Command.Negotiate, body);
        if (response.Status == Status.Success)
        {
            Dialect = Wire.U16(response.Body, 4);
        }

        return response;
    }

    /// <summary>
    /// SESSION_SETUP in two round trips: an NTLMSSP NEGOTIATE, then the AUTHENTICATE answering the server's CHALLENGE with
    /// an NTLMv2 response and a random session key (key exchange). A null user signs in anonymously, as a guest.
    /// </summary>
    public async Task<uint> SessionSetupAsync(string? user, string? password, bool spnego = true, string domain = "WORKGROUP")
    {
        var negotiate = new byte[32];
        Ntlm.Signature.CopyTo(negotiate);
        Wire.Put32(negotiate, 8, Ntlm.TypeNegotiate);
        Wire.Put32(negotiate, 12, 0xE2088297);
        var first = await SessionSetupRequestAsync(spnego ? SpnegoInit(negotiate) : negotiate);
        if (first.Status != Status.MoreProcessingRequired)
        {
            return first.Status;
        }

        SessionId = first.SessionId;
        var securityBuffer = first.Body.AsSpan(Wire.U16(first.Body, 4) - Smb2.HeaderSize, Wire.U16(first.Body, 6)).ToArray();
        var challenge = spnego ? Spnego.ExtractNtlm(securityBuffer).ToArray() : securityBuffer;
        if (Ntlm.MessageType(challenge) != Ntlm.TypeChallenge)
        {
            throw new InvalidOperationException("no CHALLENGE in the server's reply");
        }

        var serverChallenge = challenge.AsSpan(24, 8).ToArray();
        var targetInfo = challenge.AsSpan((int)Wire.U32(challenge, 44), Wire.U16(challenge, 40)).ToArray();
        var (authenticate, sessionKey) = Authenticate(user, password, domain, serverChallenge, targetInfo);
        var second = await SessionSetupRequestAsync(spnego ? SpnegoResp(authenticate) : authenticate);
        if (second.Status == Status.Success)
        {
            Guest = (Wire.U16(second.Body, 2) & SessionFlags.IsGuest) != 0;
            if (!Guest && sessionKey is not null)
            {
                SessionKey = sessionKey;
                _signer = new Signer(sessionKey, Dialect);

                // the final response is signed with the new key
                if (second.Signed && !_signer.Verify(second.Pdu))
                {
                    throw new InvalidOperationException("the SESSION_SETUP response's signature is wrong");
                }
            }
        }

        return second.Status;
    }

    public async Task<uint> TreeConnectAsync(string share)
    {
        var path = Encoding.Unicode.GetBytes(@"\\server\" + share);
        var body = new byte[8 + path.Length];
        Wire.Put16(body, 0, 9);
        Wire.Put16(body, 4, Smb2.HeaderSize + 8);
        Wire.Put16(body, 6, (ushort)path.Length);
        path.CopyTo(body, 8);
        var response = await SendAsync(Command.TreeConnect, body);
        response.EnsureSuccess();
        return response.TreeId;
    }

    public static byte[] CreateBody(string name, uint access = 0x0012019F, uint disposition = Disposition.Open, uint options = 0, byte[]? contexts = null)
    {
        var nameBytes = Encoding.Unicode.GetBytes(name);
        var contextsAt = Wire.RoundUp(56 + Math.Max(nameBytes.Length, 1), 8);
        var body = new byte[contexts is null ? 56 + Math.Max(nameBytes.Length, 1) : contextsAt + contexts.Length];
        Wire.Put16(body, 0, 57);
        Wire.Put32(body, 24, access);
        Wire.Put32(body, 32, 0x7);              // ShareAccess: read, write, delete
        Wire.Put32(body, 36, disposition);
        Wire.Put32(body, 40, options);
        Wire.Put16(body, 44, Smb2.HeaderSize + 56);
        Wire.Put16(body, 46, (ushort)nameBytes.Length);
        nameBytes.CopyTo(body, 56);
        if (contexts is not null)
        {
            Wire.Put32(body, 48, (uint)(Smb2.HeaderSize + contextsAt));
            Wire.Put32(body, 52, (uint)contexts.Length);
            contexts.CopyTo(body, contextsAt);
        }

        return body;
    }

    /// <summary>Opens a name; the response (whose body has the FileId at 64) or the failure status.</summary>
    public Task<SmbResponse> CreateAsync(uint treeId, string name, uint access = 0x0012019F, uint disposition = Disposition.Open, uint options = 0, byte[]? contexts = null)
        => SendAsync(Command.Create, CreateBody(name, access, disposition, options, contexts), treeId);

    public static byte[] FileIdOf(SmbResponse create) => create.Body.AsSpan(64, 16).ToArray();

    public async Task<byte[]> OpenAsync(uint treeId, string name, bool write = false, uint disposition = Disposition.Open, uint options = 0)
        => FileIdOf((await CreateAsync(treeId, name, write ? 0x0012019F | Access.FileWriteData | Access.FileAppendData : 0x00120089, disposition, options)).EnsureSuccess());

    public static byte[] CloseBody(byte[] fileId, bool postQuery = false)
    {
        var body = new byte[24];
        Wire.Put16(body, 0, 24);
        Wire.Put16(body, 2, postQuery ? CloseFlags.PostQueryAttributes : (ushort)0);
        fileId.CopyTo(body, 8);
        return body;
    }

    public Task<SmbResponse> CloseAsync(uint treeId, byte[] fileId, bool postQuery = false) => SendAsync(Command.Close, CloseBody(fileId, postQuery), treeId);

    public async Task<byte[]> ReadAsync(uint treeId, byte[] fileId, long offset, int length)
    {
        var response = await ReadRawAsync(treeId, fileId, offset, length);
        if (response.Status == Status.EndOfFile)
        {
            return [];
        }

        response.EnsureSuccess();
        var dataOffset = response.Body[2] - Smb2.HeaderSize;
        return response.Body.AsSpan(dataOffset, (int)Wire.U32(response.Body, 4)).ToArray();
    }

    public Task<SmbResponse> ReadRawAsync(uint treeId, byte[] fileId, long offset, int length)
    {
        var body = new byte[49];
        Wire.Put16(body, 0, 49);
        Wire.Put32(body, 4, (uint)length);
        Wire.Put64(body, 8, (ulong)offset);
        fileId.CopyTo(body, 16);
        return SendAsync(Command.Read, body, treeId, CreditCharge(length));
    }

    public async Task<SmbResponse> WriteAsync(uint treeId, byte[] fileId, long offset, ReadOnlyMemory<byte> data)
    {
        var body = new byte[48 + data.Length];
        Wire.Put16(body, 0, 49);
        Wire.Put16(body, 2, Smb2.HeaderSize + 48);
        Wire.Put32(body, 4, (uint)data.Length);
        Wire.Put64(body, 8, (ulong)offset);
        fileId.CopyTo(body, 16);
        data.Span.CopyTo(body.AsSpan(48));
        return await SendAsync(Command.Write, body, treeId, CreditCharge(data.Length));
    }

    public Task<SmbResponse> FlushAsync(uint treeId, byte[] fileId)
    {
        var body = new byte[24];
        Wire.Put16(body, 0, 24);
        fileId.CopyTo(body, 8);
        return SendAsync(Command.Flush, body, treeId);
    }

    public static byte[] QueryDirectoryBody(byte[] fileId, string pattern = "*", byte infoClass = 0x25, byte flags = 0, int outputLength = 65536)
    {
        var p = Encoding.Unicode.GetBytes(pattern);
        var body = new byte[32 + Math.Max(p.Length, 1)];
        Wire.Put16(body, 0, 33);
        body[2] = infoClass;
        body[3] = flags;
        fileId.CopyTo(body, 8);
        Wire.Put16(body, 24, Smb2.HeaderSize + 32);
        Wire.Put16(body, 26, (ushort)p.Length);
        Wire.Put32(body, 28, (uint)outputLength);
        p.CopyTo(body, 32);
        return body;
    }

    /// <summary>Every name in a directory, enumerated to the end.</summary>
    public async Task<List<string>> ListAsync(uint treeId, string directory, string pattern = "*", byte infoClass = 0x25)
    {
        var fileId = await OpenAsync(treeId, directory, options: CreateOptions.DirectoryFile);
        var names = new List<string>();
        try
        {
            var flags = QueryDirectoryFlags.RestartScans;
            while (true)
            {
                var response = await SendAsync(Command.QueryDirectory, QueryDirectoryBody(fileId, pattern, infoClass, flags), treeId);
                flags = 0;
                if (response.Status == Status.NoMoreFiles)
                {
                    break;
                }

                response.EnsureSuccess();
                names.AddRange(DirectoryNames(response, infoClass));
            }
        }
        finally
        {
            await CloseAsync(treeId, fileId);
        }

        return names;
    }

    /// <summary>The names in a QUERY_DIRECTORY response.</summary>
    public static List<string> DirectoryNames(SmbResponse response, byte infoClass)
    {
        Info.DirectoryLayout(infoClass, out var nameAt);
        var info = response.Body.AsSpan(8, (int)Wire.U32(response.Body, 4));
        var names = new List<string>();
        for (var at = 0; ;)
        {
            var e = info[at..];
            var nameLength = infoClass == 0x0C ? (int)Wire.U32(e, 8) : (int)Wire.U32(e, 60);
            names.Add(Encoding.Unicode.GetString(e.Slice(nameAt, nameLength)));
            var next = (int)Wire.U32(e, 0);
            if (next == 0)
            {
                return names;
            }

            at += next;
        }
    }

    public static byte[] QueryInfoBody(byte[] fileId, byte infoType, byte infoClass, int outputLength = 4096)
    {
        var body = new byte[41];
        Wire.Put16(body, 0, 41);
        body[2] = infoType;
        body[3] = infoClass;
        Wire.Put32(body, 4, (uint)outputLength);
        fileId.CopyTo(body, 24);
        return body;
    }

    public Task<SmbResponse> QueryInfoAsync(uint treeId, byte[] fileId, byte infoType, byte infoClass, int outputLength = 4096)
        => SendAsync(Command.QueryInfo, QueryInfoBody(fileId, infoType, infoClass, outputLength), treeId);

    public static byte[] SetInfoBody(byte[] fileId, byte infoClass, byte[] info)
    {
        var body = new byte[32 + info.Length];
        Wire.Put16(body, 0, 33);
        body[2] = InfoType.File;
        body[3] = infoClass;
        Wire.Put32(body, 4, (uint)info.Length);
        Wire.Put16(body, 8, Smb2.HeaderSize + 32);
        fileId.CopyTo(body, 16);
        info.CopyTo(body, 32);
        return body;
    }

    public Task<SmbResponse> SetInfoAsync(uint treeId, byte[] fileId, byte infoClass, byte[] info)
        => SendAsync(Command.SetInfo, SetInfoBody(fileId, infoClass, info), treeId);

    public static byte[] RenameInfo(string target, bool replace)
    {
        var name = Encoding.Unicode.GetBytes(target);
        var info = new byte[20 + name.Length];
        info[0] = replace ? (byte)1 : (byte)0;
        Wire.Put32(info, 16, (uint)name.Length);
        name.CopyTo(info, 20);
        return info;
    }

    public Task<SmbResponse> RenameAsync(uint treeId, byte[] fileId, string target, bool replace = false)
        => SetInfoAsync(treeId, fileId, FileInfoClass.Rename, RenameInfo(target, replace));

    public Task<SmbResponse> DeleteOnCloseAsync(uint treeId, byte[] fileId) => SetInfoAsync(treeId, fileId, FileInfoClass.Disposition, [1]);

    public Task<SmbResponse> SetEndOfFileAsync(uint treeId, byte[] fileId, long length)
    {
        var info = new byte[8];
        Wire.Put64(info, 0, (ulong)length);
        return SetInfoAsync(treeId, fileId, FileInfoClass.EndOfFile, info);
    }

    public Task<SmbResponse> SetModifiedAsync(uint treeId, byte[] fileId, DateTimeOffset modified)
    {
        var info = new byte[40];
        Wire.Put64(info, 16, Wire.FileTime(modified));
        return SetInfoAsync(treeId, fileId, FileInfoClass.Basic, info);
    }

    public Task<SmbResponse> EchoAsync()
    {
        var body = new byte[4];
        Wire.Put16(body, 0, 4);
        return SendAsync(Command.Echo, body);
    }

    public Task<SmbResponse> IoctlAsync(uint treeId, uint code, byte[] input)
    {
        var body = new byte[56 + input.Length];
        Wire.Put16(body, 0, 57);
        Wire.Put32(body, 4, code);
        NoFileId.CopyTo(body, 8);
        Wire.Put32(body, 24, (uint)(Smb2.HeaderSize + 56));
        Wire.Put32(body, 28, (uint)input.Length);
        Wire.Put32(body, 44, 1024); // MaxOutputResponse
        Wire.Put32(body, 48, 1);    // Flags: FSCTL
        input.CopyTo(body, 56);
        return SendAsync(Command.Ioctl, body, treeId);
    }

    public Task<SmbResponse> LogoffAsync()
    {
        var body = new byte[4];
        Wire.Put16(body, 0, 4);
        return SendAsync(Command.Logoff, body);
    }

    /// <summary>One request, its response read back (signature checked when the session signs).</summary>
    public async Task<SmbResponse> SendAsync(Command command, byte[] body, uint treeId = 0, ushort creditCharge = 1)
        => (await SendCompoundAsync([(command, body)], treeId, related: false, creditCharge))[0];

    /// <summary>
    /// A compound chain in one message; with <paramref name="related"/> every command after the first is a related one,
    /// taking the session, tree and file of the one before (its FileId left as the all-0xFF placeholder by the caller).
    /// </summary>
    public async Task<List<SmbResponse>> SendCompoundAsync(IReadOnlyList<(Command Command, byte[] Body)> commands, uint treeId, bool related, ushort creditCharge = 1)
    {
        var pdus = new List<byte[]>();
        for (var i = 0; i < commands.Count; i++)
        {
            var (command, body) = commands[i];
            var pdu = new byte[Smb2.HeaderSize + body.Length];
            Smb2.Magic.CopyTo(pdu);
            Wire.Put16(pdu, 4, Smb2.HeaderSize);
            Wire.Put16(pdu, 6, creditCharge);
            Wire.Put16(pdu, 12, (ushort)command);
            Wire.Put16(pdu, 14, 64);  // CreditRequest
            var flags = related && i > 0 ? HeaderFlags.RelatedOperations : HeaderFlags.None;
            Wire.Put32(pdu, 16, (uint)flags);
            Wire.Put64(pdu, 24, _messageId);
            _messageId += Math.Max(creditCharge, (ushort)1);
            Wire.Put32(pdu, 36, treeId);
            Wire.Put64(pdu, 40, SessionId);
            body.CopyTo(pdu, Smb2.HeaderSize);
            pdus.Add(pdu);
        }

        // padded to 8, NextCommand set, each one signed
        var message = new List<byte>();
        for (var i = 0; i < pdus.Count; i++)
        {
            var pdu = pdus[i];
            if (i < pdus.Count - 1)
            {
                var padded = Wire.RoundUp(pdu.Length, 8);
                Array.Resize(ref pdu, padded);
                Wire.Put32(pdu, 20, (uint)padded);
            }

            if (_signer is not null && SignRequests && CommandAt(i) is not (Command.Negotiate or Command.SessionSetup))
            {
                _signer.Sign(pdu);
            }

            message.AddRange(pdu);
        }

        Command CommandAt(int i) => commands[i].Command;

        await WriteFrameAsync([.. message]);
        var responses = new List<SmbResponse>();
        var reply = await ReadFrameAsync();
        for (var at = 0; at < reply.Length;)
        {
            var next = (int)Wire.U32(reply, at + 20);
            var length = next == 0 ? reply.Length - at : next;
            var pdu = reply.AsSpan(at, length).ToArray();
            if (_signer is not null && (Wire.U32(pdu, 16) & (uint)HeaderFlags.Signed) != 0 && !_signer.Verify(pdu))
            {
                throw new InvalidOperationException($"the response to {(Command)Wire.U16(pdu, 12)} has a wrong signature");
            }

            responses.Add(Parse(pdu));
            if (next == 0)
            {
                break;
            }

            at += next;
        }

        return responses;
    }

    /// <summary>Sends raw bytes as one Direct TCP message and reads one message back.</summary>
    public async Task<byte[]> RoundTripAsync(byte[] message)
    {
        await WriteFrameAsync(message);
        return await ReadFrameAsync();
    }

    public static SmbResponse Parse(byte[] pdu)
        => new(Wire.U32(pdu, 8), (Command)Wire.U16(pdu, 12), Wire.U64(pdu, 24), Wire.U64(pdu, 40), Wire.U32(pdu, 36), Wire.U16(pdu, 14), Wire.U32(pdu, 16), pdu);

    public async ValueTask DisposeAsync()
    {
        _signer?.Dispose();
        await _stream.DisposeAsync();
        _tcp.Dispose();
    }

    private static ushort CreditCharge(int length) => (ushort)Math.Max(1, (length + 65535) / 65536);

    private Task<SmbResponse> SessionSetupRequestAsync(byte[] securityBuffer)
    {
        var body = new byte[24 + securityBuffer.Length];
        Wire.Put16(body, 0, 25);
        body[3] = (byte)SecurityMode.SigningEnabled;
        Wire.Put16(body, 12, Smb2.HeaderSize + 24);
        Wire.Put16(body, 14, (ushort)securityBuffer.Length);
        securityBuffer.CopyTo(body, 24);
        return SendAsync(Command.SessionSetup, body);
    }

    /// <summary>
    /// An AUTHENTICATE ([MS-NLMP] 3.1.5.1.2): the NTLMv2 response over the server's challenge and target information, and
    /// with key exchange a random session key encrypted under the session base key; anonymous with no user.
    /// </summary>
    private static (byte[] Message, byte[]? SessionKey) Authenticate(string? user, string? password, string domain, byte[] serverChallenge, byte[] targetInfo)
    {
        byte[] ntResponse = [];
        byte[] encryptedKey = [];
        byte[]? sessionKey = null;
        var flags = 0xE2088215u;
        if (user is not null)
        {
            var key = Ntlm.NtowfV2(user, password ?? "", domain);
            var blob = NtlmV2Blob(RandomNumberGenerator.GetBytes(8), DateTimeOffset.UtcNow, targetInfo);
            byte[] input = [.. serverChallenge, .. blob];
            var proof = HMACMD5.HashData(key, input);
            ntResponse = [.. proof, .. blob];
            var baseKey = HMACMD5.HashData(key, proof);
            sessionKey = RandomNumberGenerator.GetBytes(16);
            encryptedKey = Rc4.Transform(baseKey, sessionKey);
        }
        else
        {
            flags |= 0x00000800; // NTLMSSP_NEGOTIATE_ANONYMOUS
        }

        var domainBytes = Encoding.Unicode.GetBytes(domain);
        var userBytes = Encoding.Unicode.GetBytes(user ?? "");
        var workstation = Encoding.Unicode.GetBytes("CLIENT");
        var lm = user is null ? new byte[1] : new byte[24];
        var fields = new[] { lm, ntResponse, domainBytes, userBytes, workstation, encryptedKey };
        const int fixedLength = 72; // with the Version, no MIC
        var message = new byte[fixedLength + fields.Sum(f => f.Length)];
        Ntlm.Signature.CopyTo(message);
        Wire.Put32(message, 8, Ntlm.TypeAuthenticate);
        var at = fixedLength;
        var offsets = new[] { 12, 20, 28, 36, 44, 52 };
        for (var i = 0; i < fields.Length; i++)
        {
            Wire.Put16(message, offsets[i], (ushort)fields[i].Length);
            Wire.Put16(message, offsets[i] + 2, (ushort)fields[i].Length);
            Wire.Put32(message, offsets[i] + 4, (uint)at);
            fields[i].CopyTo(message, at);
            at += fields[i].Length;
        }

        Wire.Put32(message, 60, flags);
        message[64] = 10; // Version: 10.0, as Windows 10 and 11 say
        message[71] = 0x0F;
        return (message, sessionKey);
    }

    /// <summary>The NTLMv2 client blob ([MS-NLMP] 2.2.2.7): versions, a timestamp, the client challenge, the server's target information.</summary>
    public static byte[] NtlmV2Blob(byte[] clientChallenge, DateTimeOffset? time, byte[] targetInfo)
    {
        var blob = new byte[28 + targetInfo.Length + 4];
        blob[0] = 1;
        blob[1] = 1;
        Wire.Put64(blob, 8, time is null ? 0 : Wire.FileTime(time));
        clientChallenge.CopyTo(blob, 16);
        targetInfo.CopyTo(blob, 28);
        return blob;
    }

    private static byte[] SpnegoInit(byte[] ntlm)
    {
        // GSS-API: the SPNEGO OID, [0] NegTokenInit { [0] mechTypes { NTLMSSP }, [2] mechToken }
        byte[] spnegoOid = [0x06, 0x06, 0x2B, 0x06, 0x01, 0x05, 0x05, 0x02];
        byte[] ntlmOid = [0x06, 0x0A, 0x2B, 0x06, 0x01, 0x04, 0x01, 0x82, 0x37, 0x02, 0x02, 0x0A];
        var mechTypes = Tlv(0xA0, Tlv(0x30, ntlmOid));
        var mechToken = Tlv(0xA2, Tlv(0x04, ntlm));
        var init = Tlv(0xA0, Tlv(0x30, [.. mechTypes, .. mechToken]));
        return Tlv(0x60, [.. spnegoOid, .. init]);
    }

    private static byte[] SpnegoResp(byte[] ntlm) => Tlv(0xA1, Tlv(0x30, Tlv(0xA2, Tlv(0x04, ntlm))));

    private static byte[] Tlv(byte tag, byte[] content)
    {
        byte[] length = content.Length switch
        {
            < 0x80 => [(byte)content.Length],
            < 0x100 => [0x81, (byte)content.Length],
            _ => [0x82, (byte)(content.Length >> 8), (byte)content.Length],
        };
        return [tag, .. length, .. content];
    }

    private async Task WriteFrameAsync(byte[] message)
    {
        var frame = new byte[4 + message.Length];
        frame[1] = (byte)(message.Length >> 16);
        frame[2] = (byte)(message.Length >> 8);
        frame[3] = (byte)message.Length;
        message.CopyTo(frame, 4);
        await _stream.WriteAsync(frame);
    }

    private async Task<byte[]> ReadFrameAsync()
    {
        var header = new byte[4];
        await _stream.ReadExactlyAsync(header);
        var length = (header[1] << 16) | (header[2] << 8) | header[3];
        var message = new byte[length];
        await _stream.ReadExactlyAsync(message);
        return message;
    }
}
