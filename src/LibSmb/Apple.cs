using LibSmb.Protocol;

namespace LibSmb;

/// <summary>
/// Apple's SMB2 extensions. [MS-SMB2] has none of this; the references are Samba's vfs_fruit (check_aapl and the
/// readdir_attr marshalling) and Apple's "SMB2 Apple Extensions" note. A macOS client sends an "AAPL" create context on
/// its first CREATE after a tree connect. A server that answers it claiming kAAPL_SUPPORTS_READ_DIR_ATTR then packs the
/// extra facts macOS wants of each entry (max access, resource fork size, Finder info) into every
/// FileIdBothDirectoryInformation entry it sends that client, and the client takes a directory from the enumeration
/// alone. Without it macOS looks every entry up one by one (a compound CREATE, QUERY_DIRECTORY by name, CLOSE per entry,
/// two or three of them with the AppleDouble checks): a 5,000-file folder took minutes over a LAN.
/// </summary>
internal static class Apple
{
    public const string ContextName = "AAPL";

    public const uint ServerQuery = 1;

    // request and reply bitmap
    public const ulong ServerCaps = 1;
    public const ulong VolumeCaps = 2;
    public const ulong ModelInfo = 4;

    // client and server capabilities
    public const ulong SupportsReadDirAttr = 1;
    public const ulong UnixBased = 4;

    // volume capabilities
    public const ulong CaseSensitive = 2;

    /// <summary>The model only picks the icon macOS shows for the server; Samba's default.</summary>
    public const string Model = "MacSamba";

    /// <summary>
    /// What clients put on the wire as ShortNameLength in Apple's layout (Samba notes it; Apple's document says 0).
    /// </summary>
    public const byte ShortNameLength = 24;

    /// <summary>
    /// A CREATE request's create contexts ([MS-SMB2] 2.2.13.2) by name; null when it has none or they are malformed.
    /// Offsets inside a context count from the context; the chain's offset counts from the SMB2 header.
    /// </summary>
    public static Dictionary<string, byte[]>? CreateContexts(ReadOnlySpan<byte> body)
    {
        if (body.Length < 56)
        {
            return null;
        }

        var offset = (int)Wire.U32(body, 48) - Smb2.HeaderSize;
        var length = (int)Wire.U32(body, 52);
        if (offset < 0 || length <= 0 || (long)offset + length > body.Length)
        {
            return null;
        }

        var contexts = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        for (var at = offset; at + 16 <= offset + length;)
        {
            var next = (int)Wire.U32(body, at);
            var nameOffset = Wire.U16(body, at + 4);
            var nameLength = Wire.U16(body, at + 6);
            var dataOffset = Wire.U16(body, at + 10);
            var dataLength = (int)Wire.U32(body, at + 12);
            if (nameLength == 0 || (long)at + nameOffset + nameLength > body.Length || (long)at + dataOffset + dataLength > body.Length || dataLength < 0)
            {
                break;
            }

            var name = System.Text.Encoding.ASCII.GetString(body.Slice(at + nameOffset, nameLength));
            contexts[name] = dataLength > 0 ? body.Slice(at + dataOffset, dataLength).ToArray() : [];
            if (next <= 0)
            {
                break;
            }

            at += next;
        }

        return contexts;
    }

    /// <summary>One create context for a CREATE response, the last of its chain: the 16-byte head, the name, the data 8-aligned after it.</summary>
    public static byte[] ContextBlob(string name, ReadOnlySpan<byte> data)
    {
        var dataOffset = Wire.RoundUp(16 + name.Length, 8);
        var b = new byte[Wire.RoundUp(dataOffset + data.Length, 8)];
        Wire.Put16(b, 4, 16);
        Wire.Put16(b, 6, (ushort)name.Length);
        Wire.Put16(b, 10, (ushort)dataOffset);
        Wire.Put32(b, 12, (uint)data.Length);
        System.Text.Encoding.ASCII.GetBytes(name, b.AsSpan(16));
        data.CopyTo(b.AsSpan(dataOffset));
        return b;
    }

    /// <summary>
    /// The answer to a kAAPL_SERVER_QUERY. The request is 24 bytes: the command, reserved, the bitmap asked for, the
    /// client's capabilities. The reply is the command, the bitmap answered, then as the bitmap asks: the server's
    /// capabilities (UNIX-based, and ReadDirAttr when the client has it; no copyfile, no NFS ACEs), the volume's (case
    /// sensitivity, from the file system) and the model as a counted UTF-16 string behind a zero word. Null when the
    /// request is no server query. <paramref name="readDirAttr"/> says whether the client now gets Apple's entry layout.
    /// </summary>
    public static byte[]? ServerQueryResponse(ReadOnlySpan<byte> request, bool caseInsensitive, out bool readDirAttr)
    {
        readDirAttr = false;
        if (request.Length < 24 || Wire.U32(request, 0) != ServerQuery)
        {
            return null;
        }

        var bitmap = Wire.U64(request, 8);
        var clientCaps = Wire.U64(request, 16);
        var reply = new List<byte>(64);
        Append32(reply, ServerQuery);
        Append32(reply, 0);
        Append64(reply, bitmap);
        if ((bitmap & ServerCaps) != 0)
        {
            // UNIX-based unconditionally, as Samba answers: the entries then carry a UNIX mode
            var caps = UnixBased;
            if ((clientCaps & SupportsReadDirAttr) != 0)
            {
                caps |= SupportsReadDirAttr;
                readDirAttr = true;
            }

            Append64(reply, caps);
        }

        if ((bitmap & VolumeCaps) != 0)
        {
            Append64(reply, caseInsensitive ? 0 : CaseSensitive);
        }

        if ((bitmap & ModelInfo) != 0)
        {
            var model = Wire.Utf16(Model);
            Append32(reply, 0);
            Append32(reply, (uint)model.Length);
            reply.AddRange(model);
        }

        return [.. reply];
    }

    /// <summary>
    /// Apple's layout of a FileIdBothDirectoryInformation entry: the max access in EaSize, ShortNameLength 24, the resource
    /// fork size and compressed Finder info (zero: no forks, no Finder info) and, since the server says it is UNIX-based,
    /// the UNIX mode in Reserved2: 755 for a folder, 644 for a file, 444 (or 555) when read-only.
    /// </summary>
    public static void ReadDirAttr(Span<byte> e, bool readOnly, bool isDirectory)
    {
        var access = Access.All;
        ushort mode = isDirectory ? (ushort)0x1ED /* 0755 */ : (ushort)0x1A4 /* 0644 */;
        if (readOnly)
        {
            access = Access.ReadExecute;
            mode &= unchecked((ushort)~0x92); // no write bits (0222)
        }

        Wire.Put32(e, 64, access);
        e[68] = ShortNameLength;
        e[69] = 0;
        e[70..94].Clear();
        Wire.Put16(e, 94, mode);
    }

    private static void Append32(List<byte> b, uint value)
    {
        Span<byte> v = stackalloc byte[4];
        Wire.Put32(v, 0, value);
        b.AddRange(v);
    }

    private static void Append64(List<byte> b, ulong value)
    {
        Span<byte> v = stackalloc byte[8];
        Wire.Put64(v, 0, value);
        b.AddRange(v);
    }
}
