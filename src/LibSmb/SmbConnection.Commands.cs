using System.Security.Cryptography;
using LibSmb.Protocol;
using LibSmb.Security;
using Microsoft.Extensions.Logging;

namespace LibSmb;

internal sealed partial class SmbConnection
{
    /// <summary>
    /// One command of a message (perhaps one element of a compound chain): its reply PDU (header and body) and the signer
    /// for it, or no reply (CANCEL).
    /// </summary>
    internal async ValueTask<(Reply? Reply, Signer? Signer)> HandleCommandAsync(Header h, Memory<byte> pdu, Chain chain, CancellationToken ct)
    {
        var credits = Math.Max(h.CreditRequest, (ushort)1);
        var sessionId = h.SessionId;
        var treeId = h.TreeId;

        // a related command takes the session, tree and file of the one before it rather than carrying its own
        if (h.Related)
        {
            sessionId = chain.SessionId;
            treeId = chain.TreeId;
        }
        else
        {
            chain.SessionId = sessionId;
            chain.TreeId = treeId;
            chain.FileId = null;
            chain.Failed = null;
        }

        Logger.LogTrace("smb: {Remote} >> {Command} message {MessageId} flags {Flags} ({Length} bytes)", Remote, h.Command, h.MessageId, h.Flags, pdu.Length);

        // with an account to sign in to, every command but the handshake needs a session that signed in: otherwise a
        // client could skip SESSION_SETUP and still connect to a tree and open files
        if (!_server.GuestOnly && h.Command is not (Command.Negotiate or Command.SessionSetup) && !_sessions.ContainsKey(sessionId))
        {
            Logger.LogDebug("smb: {Remote}: {Command} refused on session {Session}, which has not signed in", Remote, h.Command, sessionId);
            return Done(h, Status.UserSessionDeleted, Reply.Error(), sessionId, treeId, credits, null);
        }

        // A signed request from a session with a key must carry the right signature, so nobody in between can change it.
        // Checked on the PDU as the client signed it: a related command's FileId placeholder is replaced only after.
        var signer = SignerFor(sessionId);
        if (signer is not null && h.Signed && !signer.Verify(pdu.Span))
        {
            Logger.LogDebug("smb: {Remote}: {Command} refused for a bad signature on session {Session}", Remote, h.Command, sessionId);
            return Done(h, Status.AccessDenied, Reply.Error(), sessionId, treeId, credits, signer);
        }

        var body = pdu[Smb2.HeaderSize..];
        if (h.Related && FileIdOffset(h.Command) is { } fileIdAt && fileIdAt + 16 <= body.Length)
        {
            if (chain.FileId is { } fileId)
            {
                fileId.Write(body.Span[fileIdAt..]);
            }
            else if (chain.Failed is { } failed)
            {
                // the CREATE before it failed: so does this, with the same status ([MS-SMB2] 3.3.5.2.7.2)
                return Done(h, failed, Reply.Error(), sessionId, treeId, credits, signer);
            }
        }

        if (FileIdOffset(h.Command) is { } at && at + 16 <= body.Length)
        {
            Logger.LogTrace("smb: {Remote} >> {Command} on {FileId}", Remote, h.Command, FileId.Read(body.Span[at..]).Volatile);
        }

        uint status;
        Reply reply;
        switch (h.Command)
        {
            case Command.Negotiate:
                (status, reply) = Negotiate(body.Span);
                break;
            case Command.SessionSetup:
                (status, sessionId, reply) = SessionSetup(h, body.Span);
                chain.SessionId = sessionId;
                signer = SignerFor(sessionId);
                break;
            case Command.TreeConnect:
                (status, treeId, reply) = TreeConnect(body.Span);
                chain.TreeId = treeId;
                break;
            case Command.TreeDisconnect:
                _trees.TryRemove(treeId, out _);
                (status, reply) = (Status.Success, Reply.Fixed(4));
                break;
            case Command.Logoff:
                // signed with the key of the session it ends, which goes now
                _sessions.TryRemove(sessionId, out _);
                (status, reply) = (Status.Success, Reply.Fixed(4));
                break;
            case Command.Echo:
                (status, reply) = (Status.Success, Reply.Fixed(4));
                break;
            case Command.Create:
                (status, reply) = await CreateAsync(body, treeId, ct).ConfigureAwait(false);
                chain.FileId = Status.IsError(status) ? null : FileId.Read(reply.Body[64..80]);
                break;
            case Command.Close:
                (status, reply) = await CloseAsync(body, ct).ConfigureAwait(false);
                break;
            case Command.Read:
                (status, reply) = await ReadAsync(body, ct).ConfigureAwait(false);
                break;
            case Command.Write:
                (status, reply) = await WriteAsync(body, ct).ConfigureAwait(false);
                break;
            case Command.Flush:
                (status, reply) = await FlushAsync(body, ct).ConfigureAwait(false);
                break;
            case Command.QueryDirectory:
                (status, reply) = await QueryDirectoryAsync(body, ct).ConfigureAwait(false);
                break;
            case Command.QueryInfo:
                (status, reply) = await QueryInfoAsync(body, ct).ConfigureAwait(false);
                break;
            case Command.SetInfo:
                (status, reply) = await SetInfoAsync(body, ct).ConfigureAwait(false);
                break;
            case Command.Ioctl:
                (status, reply) = Ioctl(body.Span);
                break;
            case Command.Lock:
                // byte-range locks are not enforced, but every request is granted, so clients that rely on advisory
                // locking (Windows' among them) carry on
                (status, reply) = (Status.Success, Reply.Fixed(4));
                break;
            case Command.Cancel:
                // CANCEL has no response
                return (null, null);
            default:
                (status, reply) = (Status.NotSupported, Reply.Error());
                break;
        }

        if (Status.IsError(status))
        {
            chain.Failed = status;
        }

        return Done(h, status, reply, sessionId, treeId, credits, signer);
    }

    /// <summary>A reply's header written: the request's MessageId and CreditCharge echoed, the status, session, tree and credits.</summary>
    private (Reply Reply, Signer? Signer) Done(Header h, uint status, Reply reply, ulong sessionId, uint treeId, ushort credits, Signer? signer)
    {
        h.WriteResponse(reply.Pdu, status, sessionId, treeId, credits);
        Logger.LogTrace("smb: {Remote} << {Command} status {Status:X8} ({Length} bytes)", Remote, h.Command, status, reply.BodyLength);
        return (reply, signer);
    }

    /// <summary>Where a command's FileId sits in its body ([MS-SMB2] 2.2), for a related command to take the chain's.</summary>
    private static int? FileIdOffset(Command command) => command switch
    {
        Command.Close or Command.Flush or Command.Ioctl or Command.QueryDirectory or Command.Lock or Command.ChangeNotify => 8,
        Command.Read or Command.Write or Command.SetInfo => 16,
        Command.QueryInfo => 24,
        _ => null,
    };

    // ----- NEGOTIATE -----

    /// <summary>The highest dialect both ends speak; NOT_SUPPORTED when the client offers none of ours (a 3.1.1-only client).</summary>
    private (uint Status, Reply Reply) Negotiate(ReadOnlySpan<byte> body)
    {
        if (ChooseDialect(body) is not { } dialect)
        {
            return (Status.NotSupported, Reply.Error());
        }

        Dialect = dialect;
        return (Status.Success, NegotiateBody(dialect));
    }

    /// <summary>The dialect of ours a NEGOTIATE request offers that we prefer most, or null.</summary>
    internal static ushort? ChooseDialect(ReadOnlySpan<byte> body)
    {
        if (body.Length < 36)
        {
            return null;
        }

        var count = Wire.U16(body, 2);
        var offered = new HashSet<ushort>();
        for (var i = 0; i < count && 36 + i * 2 + 2 <= body.Length; i++)
        {
            offered.Add(Wire.U16(body, 36 + i * 2));
        }

        foreach (var dialect in Protocol.Dialect.Supported)
        {
            if (offered.Contains(dialect))
            {
                return dialect;
            }
        }

        return null;
    }

    /// <summary>The NEGOTIATE response ([MS-SMB2] 2.2.4): signing enabled (not required), no capabilities, 1 MiB transfers, no security blob.</summary>
    private Reply NegotiateBody(ushort dialect)
    {
        var reply = Reply.Rent(64);
        var b = reply.Body;
        Wire.Put16(b, 0, 65);                             // StructureSize
        Wire.Put16(b, 2, SecurityMode.SigningEnabled);
        Wire.Put16(b, 4, dialect);
        _server.ServerGuid.CopyTo(b[8..]);
        Wire.Put32(b, 24, 0);                             // Capabilities
        Wire.Put32(b, 28, Smb2.MaxIoSize);                // MaxTransactSize
        Wire.Put32(b, 32, Smb2.MaxIoSize);                // MaxReadSize
        Wire.Put32(b, 36, Smb2.MaxIoSize);                // MaxWriteSize
        Wire.Put64(b, 40, Wire.FileTime(DateTimeOffset.UtcNow)); // SystemTime
        Wire.Put16(b, 56, Smb2.HeaderSize + 64);          // SecurityBufferOffset
        return reply;
    }

    /// <summary>The SMB2 NEGOTIATE reply to a legacy SMB1 negotiate: the wildcard dialect, which makes the client negotiate again in SMB2.</summary>
    private Reply Smb1NegotiateReply()
    {
        var reply = NegotiateBody(Protocol.Dialect.Wildcard);
        var pdu = reply.Pdu;
        Smb2.Magic.CopyTo(pdu);
        Wire.Put16(pdu, 4, Smb2.HeaderSize);
        Wire.Put16(pdu, 12, (ushort)Command.Negotiate);
        Wire.Put16(pdu, 14, 1); // CreditResponse
        Wire.Put32(pdu, 16, (uint)HeaderFlags.ServerToRedirector);
        return reply;
    }

    // ----- SESSION_SETUP -----

    /// <summary>
    /// SESSION_SETUP: NTLM in two round trips, wrapped in SPNEGO (Windows, macOS) or bare (Linux cifs). The client's
    /// NEGOTIATE gets STATUS_MORE_PROCESSING_REQUIRED and a CHALLENGE; its AUTHENTICATE is checked and, with an account,
    /// gives the session its signing key. Without one, every client signs in as a guest.
    /// </summary>
    private (uint Status, ulong SessionId, Reply Reply) SessionSetup(Header h, ReadOnlySpan<byte> body)
    {
        if (body.Length < 24)
        {
            return (Status.InvalidParameter, h.SessionId, Reply.Error());
        }

        var buffer = Wire.At(body, Wire.U16(body, 12), Wire.U16(body, 14));
        var raw = buffer.Length >= 8 && buffer[..8].SequenceEqual(Ntlm.Signature);
        var token = raw ? buffer : Spnego.ExtractNtlm(buffer);
        var sessionId = h.SessionId == 0 ? _server.NextSessionId() : h.SessionId;

        if (Ntlm.MessageType(token) == Ntlm.TypeAuthenticate)
        {
            _challenges.TryRemove(sessionId, out var challenge);
            var (signedIn, guest, signer) = Authenticate(token, challenge);
            if (!signedIn)
            {
                Logger.LogDebug("smb: {Remote}: sign-in refused on session {Session}", Remote, sessionId);
                return (Status.LogonFailure, sessionId, Reply.Error());
            }

            _sessions[sessionId] = new Session(sessionId, guest, signer);
            Logger.LogDebug("smb: {Remote}: session {Session} signed in{Guest}", Remote, sessionId, guest ? " as a guest" : "");
            if (guest)
            {
                WarnWindowsGuest(token);
            }

            return (Status.Success, sessionId, SessionSetupBody(guest ? SessionFlags.IsGuest : (ushort)0, raw ? null : Spnego.NegTokenResp(Spnego.AcceptCompleted, false, null)));
        }

        // the first round trip: a challenge, remembered for the session, sent back the way the client wrapped its message
        var serverChallenge = RandomNumberGenerator.GetBytes(8);
        _challenges[sessionId] = serverChallenge;
        var ntlmChallenge = Ntlm.Challenge(serverChallenge, _server.Options.ServerName, _server.Options.DomainName);
        var reply = raw ? ntlmChallenge : Spnego.NegTokenResp(Spnego.AcceptIncomplete, true, ntlmChallenge);
        return (Status.MoreProcessingRequired, sessionId, SessionSetupBody(0, reply));
    }

    /// <summary>
    /// Checks an AUTHENTICATE: anyone is a guest when the server has no account; otherwise the account's user name (in any
    /// case) and an NTLMv2 response that its password gives, and then the signing key for the dialect.
    /// </summary>
    private (bool SignedIn, bool Guest, Signer? Signer) Authenticate(ReadOnlySpan<byte> token, byte[]? challenge)
    {
        if (_server.GuestOnly)
        {
            return (true, true, null);
        }

        var options = _server.Options;
        if (challenge is null || Ntlm.ParseAuthenticate(token) is not { } auth || !string.Equals(auth.User, options.User, StringComparison.OrdinalIgnoreCase))
        {
            return (false, false, null);
        }

        var responseKeyNt = Ntlm.NtowfV2(options.User!, options.Password ?? "", auth.Domain);
        if (!Ntlm.ValidateV2(responseKeyNt, challenge, auth.NtResponse))
        {
            return (false, false, null);
        }

        var sessionKey = Ntlm.ExportedSessionKey(responseKeyNt, auth.NtResponse.AsSpan(0, 16), auth.Flags, auth.EncryptedSessionKey);
        return (true, false, new Signer(sessionKey, Dialect));
    }

    /// <summary>
    /// Once, a hint when a Windows 10 or 11 client (NTLMSSP version 10) signs in as a guest: Windows refuses guest sessions,
    /// which cannot be signed, unless its EnableInsecureGuestLogons policy is on.
    /// </summary>
    private void WarnWindowsGuest(ReadOnlySpan<byte> token)
    {
        if (_server.WarnedWindowsGuest || Ntlm.ParseAuthenticate(token) is not { OsMajor: >= 10 })
        {
            return;
        }

        _server.WarnedWindowsGuest = true;
        Logger.LogInformation("smb: a Windows client signed in as a guest and will likely refuse the session: Windows requires signing, which a guest session cannot do; give the server an account");
    }

    /// <summary>The SESSION_SETUP response ([MS-SMB2] 2.2.6) with its session flags and security blob.</summary>
    private static Reply SessionSetupBody(ushort sessionFlags, byte[]? securityBuffer)
    {
        var length = securityBuffer?.Length ?? 0;
        var reply = Reply.Rent(8 + length);
        var b = reply.Body;
        Wire.Put16(b, 0, 9);
        Wire.Put16(b, 2, sessionFlags);
        Wire.Put16(b, 4, Smb2.HeaderSize + 8);
        Wire.Put16(b, 6, (ushort)length);
        securityBuffer?.CopyTo(b[8..]);
        return reply;
    }

    // ----- TREE_CONNECT -----

    /// <summary>
    /// TREE_CONNECT: one of the shares, or IPC$ (Linux cifs connects to it for DFS referrals and will not mount without
    /// it; named pipes are not served). Anything else is STATUS_BAD_NETWORK_NAME.
    /// </summary>
    private (uint Status, uint TreeId, Reply Reply) TreeConnect(ReadOnlySpan<byte> body)
    {
        if (body.Length < 8)
        {
            return (Status.InvalidParameter, 0, Reply.Error());
        }

        var path = Wire.Utf16(Wire.At(body, Wire.U16(body, 4), Wire.U16(body, 6)));
        var name = path.TrimStart('\\');
        name = name[(name.LastIndexOf('\\') + 1)..];
        var share = _server.FindShare(name);
        if (share is null && !name.Equals("IPC$", StringComparison.OrdinalIgnoreCase))
        {
            Logger.LogDebug("smb: {Remote}: no share named '{Share}'", Remote, name);
            return (Status.BadNetworkName, 0, Reply.Error());
        }

        var treeId = _server.NextTreeId();
        _trees[treeId] = new Tree(treeId, share);
        var reply = Reply.Rent(16);
        var b = reply.Body;
        Wire.Put16(b, 0, 16);
        b[2] = share is null ? ShareType.Pipe : ShareType.Disk;
        Wire.Put32(b, 12, share is { ReadOnly: true } ? Access.ReadExecute : Access.All); // MaximalAccess
        return (Status.Success, treeId, reply);
    }

    /// <summary>A tree's share, when the tree is a connected disk share still served.</summary>
    private (uint Status, SmbShare? Share) ShareOf(uint treeId)
    {
        if (!_trees.TryGetValue(treeId, out var tree))
        {
            return (Status.NetworkNameDeleted, null);
        }

        if (tree.Share is null)
        {
            // IPC$: no named pipes here, and a name on it must never be taken for a file's
            return (Status.ObjectNameNotFound, null);
        }

        return _server.Serves(tree.Share) ? (Status.Success, tree.Share) : (Status.NetworkNameDeleted, null);
    }
}
