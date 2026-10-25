using System.Buffers.Binary;
using System.Text;

namespace LibOutlook.Ltp;

/// <summary>Property value bytes made into .NET values by their property type ([MS-PST] 2.3.3.4, [MS-OXCDATA] 2.11.1).</summary>
internal static class PropertyValues
{
    static PropertyValues()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>A property for messages: "property 0x0037 (Subject) of type 0x001F".</summary>
    public static string Describe(ushort id, ushort type)
        => $"property 0x{id:X4}{(PstPropertyIds.NameOf(id) is { } name ? $" ({name})" : "")} of type 0x{type:X4}";

    /// <summary>Whether values of the type are small enough to live in a PC record's own 4 bytes.</summary>
    public static bool IsInline(ushort type) => type is (ushort)PstPropertyType.Integer16 or (ushort)PstPropertyType.Integer32
        or (ushort)PstPropertyType.Floating32 or (ushort)PstPropertyType.Boolean or (ushort)PstPropertyType.ErrorCode;

    /// <summary>The size of a fixed-size type's value, or 0 for the variable-sized ones.</summary>
    public static int FixedSize(ushort type) => (PstPropertyType)type switch
    {
        PstPropertyType.Integer16 => 2,
        PstPropertyType.Integer32 or PstPropertyType.Floating32 or PstPropertyType.ErrorCode => 4,
        PstPropertyType.Boolean => 1,
        PstPropertyType.Floating64 or PstPropertyType.Currency or PstPropertyType.FloatingTime or PstPropertyType.Integer64 or PstPropertyType.Time => 8,
        PstPropertyType.Guid => 16,
        _ => 0,
    };

    /// <summary>The value, or null for an empty one. Strings are trimmed of the terminator Outlook sometimes stores.</summary>
    public static object? Decode(ushort type, ReadOnlySpan<byte> bytes, int codePage)
    {
        if ((type & 0x1000) != 0)
        {
            return DecodeMultiple((ushort)(type & 0x0FFF), bytes, codePage);
        }

        switch ((PstPropertyType)type)
        {
            case PstPropertyType.Integer16:
                return bytes.Length >= 2 ? BinaryPrimitives.ReadInt16LittleEndian(bytes) : null;
            case PstPropertyType.Integer32:
                return bytes.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(bytes) : null;
            case PstPropertyType.ErrorCode:
                return bytes.Length >= 4 ? BinaryPrimitives.ReadInt32LittleEndian(bytes) : null;
            case PstPropertyType.Floating32:
                return bytes.Length >= 4 ? BinaryPrimitives.ReadSingleLittleEndian(bytes) : null;
            case PstPropertyType.Floating64:
                return bytes.Length >= 8 ? BinaryPrimitives.ReadDoubleLittleEndian(bytes) : null;
            case PstPropertyType.Currency:
                return bytes.Length >= 8 ? BinaryPrimitives.ReadInt64LittleEndian(bytes) / 10000m : null;
            case PstPropertyType.FloatingTime:
                return bytes.Length >= 8 ? OleDate(BinaryPrimitives.ReadDoubleLittleEndian(bytes)) : null;
            case PstPropertyType.Boolean:
                return bytes.Length >= 1 && bytes[0] != 0;
            case PstPropertyType.Integer64:
                return bytes.Length >= 8 ? BinaryPrimitives.ReadInt64LittleEndian(bytes) : null;
            case PstPropertyType.String8:
                return DecodeString8(bytes, codePage);
            case PstPropertyType.String:
                return DecodeString(bytes);
            case PstPropertyType.Time:
                return bytes.Length >= 8 ? FileTime(BinaryPrimitives.ReadInt64LittleEndian(bytes)) : null;
            case PstPropertyType.Guid:
                return bytes.Length >= 16 ? new Guid(bytes[..16]) : null;
            case PstPropertyType.Binary:
            case PstPropertyType.Object:
            default:
                return bytes.ToArray();
        }
    }

    /// <summary>A UTF-16 string, ended at its first NUL character: the bytes after one are not part of it.</summary>
    public static string DecodeString(ReadOnlySpan<byte> bytes)
    {
        var s = Encoding.Unicode.GetString(bytes);
        var end = s.IndexOf('\0');
        return end < 0 ? s : s[..end];
    }

    /// <summary>
    /// An 8-bit string, ended at its first NUL byte: ANSI files written by Outlook terminate strings and leave whatever
    /// the allocation held before after the terminator.
    /// </summary>
    public static string DecodeString8(ReadOnlySpan<byte> bytes, int codePage)
    {
        var end = bytes.IndexOf((byte)0);
        return EncodingFor(codePage).GetString(end < 0 ? bytes : bytes[..end]);
    }

    public static Encoding EncodingFor(int codePage)
    {
        try
        {
            return codePage switch
            {
                0 => Encoding.Latin1,
                1200 => Encoding.Unicode,
                65001 => Encoding.UTF8,
                _ => Encoding.GetEncoding(codePage),
            };
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Encoding.Latin1;
        }
    }

    /// <summary>A FILETIME as a UTC DateTime; null for 0 and for the far-future values Outlook uses as "none".</summary>
    public static DateTime? FileTime(long fileTime)
    {
        if (fileTime <= 0)
        {
            return null;
        }

        try
        {
            var time = DateTime.FromFileTimeUtc(fileTime);
            return time.Year >= 4000 ? null : time;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static DateTime? OleDate(double days)
    {
        try
        {
            return DateTime.SpecifyKind(DateTime.FromOADate(days), DateTimeKind.Utc);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static object? DecodeMultiple(ushort baseType, ReadOnlySpan<byte> bytes, int codePage)
    {
        var fixedSize = FixedSize(baseType);
        if (fixedSize > 0)
        {
            // the values one after another, nothing before them ([MS-PST] 2.3.3.4.2)
            var values = new object[bytes.Length / fixedSize];
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = Decode(baseType, bytes.Slice(i * fixedSize, fixedSize), codePage) ?? throw new PstException("a multi-valued property with an empty value");
            }

            return Typed(baseType, values);
        }

        if (bytes.Length < 4)
        {
            return Typed(baseType, []);
        }

        // count, the offset of each value from the start, then the values
        var count = BinaryPrimitives.ReadInt32LittleEndian(bytes);
        if (count < 0 || 4 + count * 4 > bytes.Length)
        {
            throw new PstException($"a multi-valued property of {count} values whose offsets do not fit {bytes.Length} bytes");
        }

        var items = new object[count];
        for (var i = 0; i < count; i++)
        {
            var start = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(4 + i * 4));
            var end = i + 1 < count ? BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(4 + (i + 1) * 4)) : bytes.Length;
            if (start < 0 || end < start || end > bytes.Length)
            {
                throw new PstException($"a multi-valued property whose value {i} lies at {start}-{end} of {bytes.Length} bytes");
            }

            items[i] = Decode(baseType, bytes[start..end], codePage) ?? "";
        }

        return Typed(baseType, items);
    }

    private static object Typed(ushort baseType, object[] values) => (PstPropertyType)baseType switch
    {
        PstPropertyType.String or PstPropertyType.String8 => values.Cast<string>().ToArray(),
        PstPropertyType.Binary => values.Cast<byte[]>().ToArray(),
        PstPropertyType.Integer32 => values.Cast<int>().ToArray(),
        PstPropertyType.Integer16 => values.Cast<short>().ToArray(),
        PstPropertyType.Integer64 => values.Cast<long>().ToArray(),
        PstPropertyType.Floating64 => values.Cast<double>().ToArray(),
        PstPropertyType.Guid => values.Cast<Guid>().ToArray(),
        PstPropertyType.Time or PstPropertyType.FloatingTime => values.Cast<DateTime>().ToArray(),
        _ => values,
    };
}
