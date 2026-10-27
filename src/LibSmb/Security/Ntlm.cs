using System.Security.Cryptography;
using LibSmb.Protocol;

namespace LibSmb.Security;

/// <summary>The fields of an NTLMSSP AUTHENTICATE message ([MS-NLMP] 2.2.1.3) the server needs.</summary>
internal sealed record NtlmAuthenticate(string User, string Domain, byte[] NtResponse, uint Flags, byte[] EncryptedSessionKey, byte OsMajor);

/// <summary>
/// Just enough NTLMSSP ([MS-NLMP]) to complete an SMB2 SESSION_SETUP: the server's CHALLENGE, the client's AUTHENTICATE
/// parsed, its NTLMv2 response checked against the one account, and the session key the client derived, from which
/// the signing key comes.
/// </summary>
internal static class Ntlm
{
    public const uint TypeNegotiate = 1;
    public const uint TypeChallenge = 2;
    public const uint TypeAuthenticate = 3;

    /// <summary>The negotiate flags of the CHALLENGE: the common reference set SMB clients expect.</summary>
    private const uint DefaultFlags = 0xE2898215;

    /// <summary>NTLMSSP_TARGET_TYPE_SERVER: the CHALLENGE's target name is a server's.</summary>
    private const uint TargetTypeServer = 0x00020000;

    /// <summary>NTLMSSP_NEGOTIATE_KEY_EXCH: the AUTHENTICATE carries a session key of the client's, encrypted.</summary>
    private const uint NegotiateKeyExchange = 0x40000000;

    /// <summary>NTLMSSP_NEGOTIATE_VERSION: an 8-byte OS version follows the negotiate flags.</summary>
    private const uint NegotiateVersion = 0x02000000;

    public static ReadOnlySpan<byte> Signature => "NTLMSSP\0"u8;

    /// <summary>The NTLMSSP message type of a token; 0 when it is none.</summary>
    public static uint MessageType(ReadOnlySpan<byte> token)
        => token.Length >= 12 && token[..8].SequenceEqual(Signature) ? Wire.U32(token, 8) : 0;

    /// <summary>
    /// A CHALLENGE message ([MS-NLMP] 2.2.1.2) with the server challenge, a target name and the target information
    /// (NetBIOS and DNS names, a timestamp), which Windows needs to compute its NTLMv2 response.
    /// </summary>
    public static byte[] Challenge(ReadOnlySpan<byte> serverChallenge, string computerName, string domainName)
    {
        var computer = Wire.Utf16(computerName);
        var domain = Wire.Utf16(domainName);
        var timestamp = new byte[8];
        Wire.Put64(timestamp, 0, Wire.FileTime(DateTimeOffset.UtcNow));
        var targetInfo = Concat(
            AvPair(2, domain),      // MsvAvNbDomainName
            AvPair(1, computer),    // MsvAvNbComputerName
            AvPair(4, domain),      // MsvAvDnsDomainName
            AvPair(3, computer),    // MsvAvDnsComputerName
            AvPair(7, timestamp),   // MsvAvTimestamp
            AvPair(0, []));         // MsvAvEOL

        const int fixedLength = 56; // with the 8-byte Version
        var targetNameOffset = fixedLength;
        var targetInfoOffset = targetNameOffset + domain.Length;
        var b = new byte[fixedLength + domain.Length + targetInfo.Length];
        Signature.CopyTo(b);
        Wire.Put32(b, 8, TypeChallenge);
        Wire.Put16(b, 12, (ushort)domain.Length);
        Wire.Put16(b, 14, (ushort)domain.Length);
        Wire.Put32(b, 16, (uint)targetNameOffset);
        Wire.Put32(b, 20, DefaultFlags | TargetTypeServer);
        serverChallenge[..8].CopyTo(b.AsSpan(24));
        Wire.Put16(b, 40, (ushort)targetInfo.Length);
        Wire.Put16(b, 42, (ushort)targetInfo.Length);
        Wire.Put32(b, 44, (uint)targetInfoOffset);

        // Version: Windows-style 10.0, NTLM revision 15
        b[48] = 10;
        b[55] = 0x0F;
        domain.CopyTo(b, targetNameOffset);
        targetInfo.CopyTo(b, targetInfoOffset);
        return b;
    }

    /// <summary>An AUTHENTICATE message's fields, Unicode names assumed (the CHALLENGE negotiates Unicode); null when it is none.</summary>
    public static NtlmAuthenticate? ParseAuthenticate(ReadOnlySpan<byte> message)
    {
        if (message.Length < 64 || MessageType(message) != TypeAuthenticate)
        {
            return null;
        }

        static byte[] Field(ReadOnlySpan<byte> m, int offset)
        {
            var length = Wire.U16(m, offset);
            var start = Wire.U32(m, offset + 4);
            return length == 0 || (long)start + length > m.Length ? [] : m.Slice((int)start, length).ToArray();
        }

        var flags = Wire.U32(message, 60);

        // the Version, when the flags say there is one, follows them; its first byte is the OS's major version
        var osMajor = (flags & NegotiateVersion) != 0 && message.Length >= 65 ? message[64] : (byte)0;
        return new NtlmAuthenticate(
            Wire.Utf16(Field(message, 36)),  // UserNameFields
            Wire.Utf16(Field(message, 28)),  // DomainNameFields
            Field(message, 20),              // NtChallengeResponseFields
            flags,
            Field(message, 52),              // EncryptedRandomSessionKeyFields
            osMajor);
    }

    /// <summary>The NT hash of a password: MD4 of its UTF-16LE.</summary>
    public static byte[] NtHash(string password) => Md4.Hash(Wire.Utf16(password));

    /// <summary>NTOWFv2: HMAC-MD5 keyed with the NT hash over the upper-cased user name and the domain, in UTF-16LE.</summary>
    public static byte[] NtowfV2(string user, string password, string domain)
        => HMACMD5.HashData(NtHash(password), Wire.Utf16(user.ToUpperInvariant() + domain));

    /// <summary>
    /// Whether an NTLMv2 response (the NTProofStr and the client's blob behind it) is the one this account's key and the
    /// server challenge give.
    /// </summary>
    public static bool ValidateV2(ReadOnlySpan<byte> responseKeyNt, ReadOnlySpan<byte> serverChallenge, ReadOnlySpan<byte> ntResponse)
    {
        if (ntResponse.Length < 16)
        {
            return false;
        }

        var proof = HMACMD5.HashData(responseKeyNt, Concat(serverChallenge.ToArray(), ntResponse[16..].ToArray()));
        return CryptographicOperations.FixedTimeEquals(proof, ntResponse[..16]);
    }

    /// <summary>
    /// The session key the client derived: the key-exchange key (for NTLMv2, the session base key, HMAC-MD5 of the
    /// NTProofStr), or with key exchange the client's random key, which it sent RC4-encrypted under that one.
    /// </summary>
    public static byte[] ExportedSessionKey(ReadOnlySpan<byte> responseKeyNt, ReadOnlySpan<byte> ntProofStr, uint flags, ReadOnlySpan<byte> encryptedRandomSessionKey)
    {
        var keyExchangeKey = HMACMD5.HashData(responseKeyNt, ntProofStr);
        if ((flags & NegotiateKeyExchange) != 0 && encryptedRandomSessionKey.Length == 16)
        {
            return Rc4.Transform(keyExchangeKey, encryptedRandomSessionKey);
        }

        return keyExchangeKey;
    }

    private static byte[] AvPair(ushort id, byte[] value)
    {
        var b = new byte[4 + value.Length];
        Wire.Put16(b, 0, id);
        Wire.Put16(b, 2, (ushort)value.Length);
        value.CopyTo(b, 4);
        return b;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var b = new byte[parts.Sum(p => p.Length)];
        var at = 0;
        foreach (var part in parts)
        {
            part.CopyTo(b, at);
            at += part.Length;
        }

        return b;
    }
}

/// <summary>
/// Just enough SPNEGO ([RFC 4178]) to carry NTLMSSP in a SESSION_SETUP: the client's NTLMSSP token taken out of a
/// negTokenInit or negTokenResp, and the server's negTokenResp wrapped around its own.
/// </summary>
internal static class Spnego
{
    public const byte AcceptCompleted = 0;
    public const byte AcceptIncomplete = 1;

    /// <summary>The NTLMSSP mechanism, 1.3.6.1.4.1.311.2.2.10, as a whole DER element.</summary>
    private static ReadOnlySpan<byte> NtlmOid => [0x06, 0x0A, 0x2B, 0x06, 0x01, 0x04, 0x01, 0x82, 0x37, 0x02, 0x02, 0x0A];

    /// <summary>The NTLMSSP token inside a SPNEGO negTokenInit (GSS-API tag 0x60) or negTokenResp (0xA1); empty when there is none.</summary>
    public static ReadOnlySpan<byte> ExtractNtlm(ReadOnlySpan<byte> buffer)
    {
        if (!ReadTlv(buffer, out var tag, out var content, out _))
        {
            return default;
        }

        ReadOnlySpan<byte> sequence;
        switch (tag)
        {
            case 0x60:
                // GSS-API: the SPNEGO OID, then [0] NegTokenInit, a SEQUENCE
                if (!ReadTlv(content, out _, out _, out var afterOid)
                    || !ReadTlv(afterOid, out var t0, out var init, out _) || t0 != 0xA0
                    || !ReadTlv(init, out var tSeq, out sequence, out _) || tSeq != 0x30)
                {
                    return default;
                }

                break;
            case 0xA1:
                // [1] NegTokenResp, a SEQUENCE
                if (!ReadTlv(content, out var tResp, out sequence, out _) || tResp != 0x30)
                {
                    return default;
                }

                break;
            default:
                return default;
        }

        // [2]: mechToken (init) or responseToken (resp), an OCTET STRING
        while (!sequence.IsEmpty)
        {
            if (!ReadTlv(sequence, out var t, out var c, out var next))
            {
                return default;
            }

            if (t == 0xA2)
            {
                return ReadTlv(c, out var tOctets, out var token, out _) && tOctets == 0x04 ? token : default;
            }

            sequence = next;
        }

        return default;
    }

    /// <summary>A negTokenResp: the state, the NTLMSSP mechanism when <paramref name="supportedMech"/> (the server's first reply names it), and the token when there is one.</summary>
    public static byte[] NegTokenResp(byte state, bool supportedMech, byte[]? token)
    {
        var sequence = new List<byte>();
        sequence.AddRange(Tlv(0xA0, Tlv(0x0A, [state])));
        if (supportedMech)
        {
            sequence.AddRange(Tlv(0xA1, NtlmOid.ToArray()));
        }

        if (token is not null)
        {
            sequence.AddRange(Tlv(0xA2, Tlv(0x04, token)));
        }

        return Tlv(0xA1, Tlv(0x30, [.. sequence]));
    }

    private static byte[] Tlv(byte tag, byte[] content)
    {
        var length = content.Length switch
        {
            < 0x80 => new[] { (byte)content.Length },
            < 0x100 => [0x81, (byte)content.Length],
            _ => [0x82, (byte)(content.Length >> 8), (byte)content.Length],
        };
        var b = new byte[1 + length.Length + content.Length];
        b[0] = tag;
        length.CopyTo(b, 1);
        content.CopyTo(b, 1 + length.Length);
        return b;
    }

    private static bool ReadTlv(ReadOnlySpan<byte> b, out byte tag, out ReadOnlySpan<byte> content, out ReadOnlySpan<byte> rest)
    {
        tag = 0;
        content = rest = default;
        if (b.Length < 2)
        {
            return false;
        }

        tag = b[0];
        int n = b[1];
        var i = 2;
        if ((n & 0x80) != 0)
        {
            var count = n & 0x7F;
            if (count is 0 or > 4 || b.Length < 2 + count)
            {
                return false;
            }

            n = 0;
            for (var j = 0; j < count; j++)
            {
                n = (n << 8) | b[2 + j];
            }

            i = 2 + count;
        }

        if (n < 0 || b.Length < i + n)
        {
            return false;
        }

        content = b.Slice(i, n);
        rest = b[(i + n)..];
        return true;
    }
}
