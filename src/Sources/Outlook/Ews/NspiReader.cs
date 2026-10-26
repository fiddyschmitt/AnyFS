using System.Buffers.Binary;
using System.Text;

namespace AnyFS.Sources.Outlook.Ews;

/// <summary>
/// Little-endian reads over an address book response body, in the shapes of [MS-OXCMAPIHTTP] 2.2.1: strings, binaries
/// and many-valued values start with a HasValue byte, Unicode strings end in a null character, counts are four bytes. A
/// body cut short, or a value of a type not read, throws an <see cref="EwsException"/> that says which answer it was.
/// </summary>
internal sealed class NspiReader(byte[] data, int position, string answer)
{
    private int _at = position;

    public byte U8()
    {
        Need(1);
        return data[_at++];
    }

    public ushort U16()
    {
        Need(2);
        var value = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(_at));
        _at += 2;
        return value;
    }

    public uint U32()
    {
        Need(4);
        var value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(_at));
        _at += 4;
        return value;
    }

    public ulong U64()
    {
        Need(8);
        var value = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(_at));
        _at += 8;
        return value;
    }

    public void Skip(int count)
    {
        Need(count);
        _at += count;
    }

    public byte[] Bytes(int count)
    {
        Need(count);
        var value = data.AsSpan(_at, count).ToArray();
        _at += count;
        return value;
    }

    /// <summary>An AddressBookPropertyValueList (a hierarchy table's row): a count, then tagged values (type, id, value), by property id.</summary>
    public Dictionary<ushort, object?> ValueList()
    {
        var values = new Dictionary<ushort, object?>();
        var count = Count();
        for (var i = 0; i < count; i++)
        {
            var type = U16();
            var id = U16();
            values[id] = Value(type);
        }

        return values;
    }

    /// <summary>An AddressBookPropertyRow in <paramref name="columns"/>: by property id, a value missing or failed (flagged, or retyped as an error) null.</summary>
    public Dictionary<ushort, object?> Row(uint[] columns)
    {
        var flagged = U8() != 0;
        var values = new Dictionary<ushort, object?>(columns.Length);
        foreach (var column in columns)
        {
            var id = (ushort)(column >> 16);
            if (flagged)
            {
                var flag = U8();
                if (flag == 0x1)
                {
                    values[id] = null;
                    continue;
                }

                if (flag == 0xA)
                {
                    U32(); // why the value is missing (MAPI_E_NOT_FOUND)
                    values[id] = null;
                    continue;
                }
            }

            values[id] = Value((ushort)column);
        }

        return values;
    }

    /// <summary>One value of the type: a number, a string (null for none), the bytes of a binary, or the values of a many-valued one.</summary>
    public object? Value(ushort type)
    {
        switch (type)
        {
            case 0x0002:
                return U16();
            case 0x0003:
                return U32();
            case 0x000A:
                U32(); // an error code where the value would be
                return null;
            case 0x000B:
                return U8() != 0;
            case 0x0014 or 0x0040:
                return U64();
            case 0x0048:
                return new Guid(Bytes(16));
            case 0x001F:
                return U8() == 0 ? null : Utf16z();
            case 0x001E:
                return U8() == 0 ? null : Latin1z();
            case 0x0102:
                return U8() == 0 ? null : Bytes(Count());
            case 0x1002 or 0x1003 or 0x1014 or 0x1040 or 0x1048 or 0x101E or 0x101F or 0x1102:
            {
                if (U8() == 0)
                {
                    return null;
                }

                // each element read as the single-valued type (a string's and a binary's with a HasValue byte of its own)
                var count = Count();
                var items = new object?[count];
                for (var i = 0; i < count; i++)
                {
                    items[i] = Value((ushort)(type & 0x0FFF));
                }

                return type is 0x101E or 0x101F ? items.OfType<string>().ToArray() : items;
            }

            default:
                throw new EwsException("NspiBody", $"{answer} with a value of type 0x{type:X4}, which AnyFS does not read");
        }
    }

    /// <summary>A permanent entry id's DN ([MS-OXNSPI] 2.2.9.3: an id type of 0, reserved bytes, the provider, a version, a display type, the DN); null for another kind of id.</summary>
    public static string? EntryDn(byte[] entryId)
        => entryId.Length > 28 && entryId[0] == 0 ? Encoding.ASCII.GetString(entryId, 28, entryId.Length - 28).TrimEnd('\0') : null;

    private int Count()
    {
        var count = U32();
        if (count > data.Length - _at)
        {
            throw Short();
        }

        return (int)count;
    }

    private string Utf16z()
    {
        var rest = data.AsSpan(_at);
        for (var i = 0; i + 1 < rest.Length; i += 2)
        {
            if (rest[i] == 0 && rest[i + 1] == 0)
            {
                var text = Encoding.Unicode.GetString(rest[..i]);
                _at += i + 2;
                return text;
            }
        }

        throw Short();
    }

    private string Latin1z()
    {
        var end = data.AsSpan(_at).IndexOf((byte)0);
        if (end < 0)
        {
            throw Short();
        }

        var text = Encoding.Latin1.GetString(data, _at, end);
        _at += end + 1;
        return text;
    }

    private void Need(int count)
    {
        if (count < 0 || _at + count > data.Length)
        {
            throw Short();
        }
    }

    private EwsException Short() => new("NspiBody", $"{answer} with a body cut short");
}
