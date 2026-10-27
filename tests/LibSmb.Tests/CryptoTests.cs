using System.Security.Cryptography;
using System.Text;
using LibSmb.Protocol;
using LibSmb.Security;

namespace LibSmb.Tests;

/// <summary>The cryptography against published test vectors: RFC 4493 (AES-CMAC), RFC 1320 (MD4), [MS-NLMP] 4.2.4 (NTLMv2).</summary>
[TestClass]
public class CryptoTests
{
    [TestMethod]
    public void AesCmacMatchesRfc4493()
    {
        var key = Convert.FromHexString("2b7e151628aed2a6abf7158809cf4f3c");
        var message = Convert.FromHexString(
            "6bc1bee22e409f96e93d7e117393172a" + "ae2d8a571e03ac9c9eb76fac45af8e51" + "30c81c46a35ce411e5fbc1191a0a52ef" + "f69f2445df4f9b17ad2b417be66c3710");
        using var cmac = new AesCmac(key);
        foreach (var (length, expected) in new[] { (0, "bb1d6929e95937287fa37d129b756746"), (16, "070a16b46b4d4144f79bdd9dd04a287c"), (40, "dfa66747de9ae63030ca32611497c827"), (64, "51f0bebf7e3b9d92fc49741779363cfe") })
        {
            var mac = new byte[16];
            cmac.Compute(message.AsSpan(0, length), mac);
            Assert.AreEqual(expected, Convert.ToHexStringLower(mac), $"{length} bytes");
        }
    }

    /// <summary>The chunked CMAC against RFC 4493 done block by block, across lengths that straddle a block, a chunk and a 1 MiB READ response.</summary>
    [TestMethod]
    public void AesCmacMatchesTheBlockByBlockReference()
    {
        var key = Enumerable.Range(0, 16).Select(i => (byte)(i * 3 + 1)).ToArray();
        var random = new Random(1);
        const int chunk = 64 << 10;
        using var cmac = new AesCmac(key);
        foreach (var length in new[] { 0, 1, 15, 16, 17, 31, 32, 33, 1000, chunk - 16, chunk - 1, chunk, chunk + 1, chunk + 16, 3 * chunk + 7, 1 << 20, (1 << 20) + 64 + 16 })
        {
            var message = new byte[length];
            random.NextBytes(message);
            var mac = new byte[16];
            cmac.Compute(message, mac);
            Assert.AreEqual(Convert.ToHexString(Reference(key, message)), Convert.ToHexString(mac), $"{length} bytes");
        }
    }

    [TestMethod]
    public void Md4MatchesRfc1320()
    {
        foreach (var (input, expected) in new[]
        {
            ("", "31d6cfe0d16ae931b73c59d7e0c089c0"),
            ("a", "bde52cb31de33e46245e05fbdbd6fb24"),
            ("abc", "a448017aaf21d8525fc10ae87aa6729d"),
            ("message digest", "d9130a8164549fe818874806e1c7014b"),
            ("abcdefghijklmnopqrstuvwxyz", "d79e1c308aa5bbcdeea8ed63df412da9"),
            ("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789", "043f8582f241db351ce627e153e7f0e4"),
            ("12345678901234567890123456789012345678901234567890123456789012345678901234567890", "e33b4ddc9c38f2199c3e7b164fcc0536"),
        })
        {
            Assert.AreEqual(expected, Convert.ToHexStringLower(Md4.Hash(Encoding.ASCII.GetBytes(input))), input);
        }

        Assert.AreEqual("8846f7eaee8fb117ad06bdd830b7586c", Convert.ToHexStringLower(Ntlm.NtHash("password")), "the NT hash of 'password'");
    }

    /// <summary>
    /// [MS-NLMP] 4.2.4's worked NTLMv2 example: user "User", domain "Domain", password "Password", the server challenge
    /// 0123456789abcdef, the client challenge aa*8 at time zero, the target information of domain "Domain" and server
    /// "Server", and the random session key 55*16.
    /// </summary>
    [TestMethod]
    public void NtlmV2MatchesTheSpecificationsExample()
    {
        var responseKeyNt = Ntlm.NtowfV2("User", "Password", "Domain");
        Assert.AreEqual("0c868a403bfd7a93a3001ef22ef02e3f", Convert.ToHexStringLower(responseKeyNt), "NTOWFv2");

        var serverChallenge = Convert.FromHexString("0123456789abcdef");
        byte[] targetInfo = [
            0x02, 0x00, 0x0C, 0x00, .. Encoding.Unicode.GetBytes("Domain"),
            0x01, 0x00, 0x0C, 0x00, .. Encoding.Unicode.GetBytes("Server"),
            0x00, 0x00, 0x00, 0x00];
        var blob = SmbTestClient.NtlmV2Blob(Enumerable.Repeat((byte)0xAA, 8).ToArray(), null, targetInfo);
        byte[] input = [.. serverChallenge, .. blob];
        var proof = HMACMD5.HashData(responseKeyNt, input);
        Assert.AreEqual("68cd0ab851e51c96aabc927bebef6a1c", Convert.ToHexStringLower(proof), "NTProofStr");

        byte[] ntResponse = [.. proof, .. blob];
        Assert.IsTrue(Ntlm.ValidateV2(responseKeyNt, serverChallenge, ntResponse));
        Assert.IsFalse(Ntlm.ValidateV2(Ntlm.NtowfV2("User", "password", "Domain"), serverChallenge, ntResponse), "another password");
        Assert.IsFalse(Ntlm.ValidateV2(responseKeyNt, Convert.FromHexString("0123456789abcdee"), ntResponse), "another challenge");

        // without key exchange the session key is the session base key; with it, the client's random key, sent encrypted
        Assert.AreEqual("8de40ccadbc14a82f15cb0ad0de95ca3", Convert.ToHexStringLower(Ntlm.ExportedSessionKey(responseKeyNt, proof, 0, [])), "SessionBaseKey");
        var randomKey = Enumerable.Repeat((byte)0x55, 16).ToArray();
        var encrypted = Rc4.Transform(Convert.FromHexString("8de40ccadbc14a82f15cb0ad0de95ca3"), randomKey);
        Assert.AreEqual("c5dad2544fc9799094ce1ce90bc9d03e", Convert.ToHexStringLower(encrypted), "EncryptedRandomSessionKey");
        Assert.AreEqual(Convert.ToHexString(randomKey), Convert.ToHexString(Ntlm.ExportedSessionKey(responseKeyNt, proof, 0x40000000, encrypted)));
    }

    /// <summary>A CHALLENGE as the server sends it: the challenge, the flags, the target name and the target information Windows needs.</summary>
    [TestMethod]
    public void TheChallengeCarriesWhatWindowsNeeds()
    {
        var challenge = Ntlm.Challenge(Convert.FromHexString("0123456789abcdef"), "SMBSERVER", "WORKGROUP");
        Assert.AreEqual(Ntlm.TypeChallenge, Ntlm.MessageType(challenge));
        Assert.AreEqual("0123456789abcdef", Convert.ToHexStringLower(challenge.AsSpan(24, 8)));
        Assert.AreEqual(0xE2898215u | 0x00020000u, Wire.U32(challenge, 20));
        Assert.AreEqual("WORKGROUP", Encoding.Unicode.GetString(challenge, (int)Wire.U32(challenge, 16), Wire.U16(challenge, 12)));
        var info = challenge.AsSpan((int)Wire.U32(challenge, 44), Wire.U16(challenge, 40));
        var ids = new List<ushort>();
        for (var at = 0; ;)
        {
            var id = Wire.U16(info, at);
            ids.Add(id);
            if (id == 0)
            {
                break;
            }

            at += 4 + Wire.U16(info, at + 2);
        }

        CollectionAssert.AreEqual(new ushort[] { 2, 1, 4, 3, 7, 0 }, ids, "NetBIOS and DNS names, a timestamp, the end");
    }

    /// <summary>The SMB 3.x signing key: the SP 800-108 KDF of the session key with SMB2AESCMAC and SmbSign.</summary>
    [TestMethod]
    public void Smb3SigningKeysComeFromTheKdf()
    {
        var sessionKey = Convert.FromHexString("8de40ccadbc14a82f15cb0ad0de95ca3");
        using var signer = new Signer(sessionKey, Dialect.Smb300);
        byte[] input = [0, 0, 0, 1, .. "SMB2AESCMAC\0"u8, 0, .. "SmbSign\0"u8, 0, 0, 0, 0x80];
        Assert.AreEqual(Convert.ToHexString(HMACSHA256.HashData(sessionKey, input)[..16]), Convert.ToHexString(signer.Key));

        using var smb2 = new Signer(sessionKey, Dialect.Smb210);
        Assert.AreEqual(Convert.ToHexString(sessionKey), Convert.ToHexString(smb2.Key), "2.x signs with the session key itself");
    }

    /// <summary>SPNEGO in and out: the NTLMSSP token out of a negTokenInit and a negTokenResp, and a negTokenResp built.</summary>
    [TestMethod]
    public void SpnegoCarriesTheNtlmToken()
    {
        var ntlm = Ntlm.Challenge(new byte[8], "S", "D");
        var resp = Spnego.NegTokenResp(Spnego.AcceptIncomplete, true, ntlm);
        Assert.AreEqual(Convert.ToHexString(ntlm), Convert.ToHexString(Spnego.ExtractNtlm(resp)));
        Assert.IsTrue(Spnego.ExtractNtlm(Spnego.NegTokenResp(Spnego.AcceptCompleted, false, null)).IsEmpty);
        Assert.IsTrue(Spnego.ExtractNtlm([0x01, 0x02]).IsEmpty, "not SPNEGO");

        // a long token (lengths in two bytes)
        var big = new byte[600];
        Ntlm.Signature.CopyTo(big);
        Assert.AreEqual(600, Spnego.ExtractNtlm(Spnego.NegTokenResp(Spnego.AcceptIncomplete, false, big)).Length);
    }

    private static byte[] Reference(byte[] key, byte[] message)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        byte[] Encrypt(byte[] block) => aes.EncryptEcb(block, PaddingMode.None);
        byte[] MulX(byte[] input)
        {
            var output = new byte[16];
            byte carry = 0;
            for (var i = 15; i >= 0; i--)
            {
                output[i] = (byte)((input[i] << 1) | carry);
                carry = (byte)(input[i] >> 7);
            }

            if (carry != 0)
            {
                output[15] ^= 0x87;
            }

            return output;
        }

        var k1 = MulX(Encrypt(new byte[16]));
        var k2 = MulX(k1);
        var full = message.Length / 16;
        var rem = message.Length % 16;
        if (rem == 0 && full > 0)
        {
            full--;
            rem = 16;
        }

        var x = new byte[16];
        for (var i = 0; i < full; i++)
        {
            var t = new byte[16];
            for (var j = 0; j < 16; j++)
            {
                t[j] = (byte)(x[j] ^ message[i * 16 + j]);
            }

            x = Encrypt(t);
        }

        var last = new byte[16];
        message.AsSpan(full * 16).CopyTo(last);
        var k = rem == 16 ? k1 : k2;
        if (rem != 16)
        {
            last[rem] = 0x80;
        }

        for (var j = 0; j < 16; j++)
        {
            last[j] = (byte)(last[j] ^ k[j] ^ x[j]);
        }

        return Encrypt(last);
    }
}
