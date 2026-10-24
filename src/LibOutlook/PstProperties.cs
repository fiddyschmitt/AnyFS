using System.Collections;
using LibOutlook.Ltp;

namespace LibOutlook;

/// <summary>A property of a folder, message, recipient or attachment, decoded.</summary>
/// <param name="Id">The property id (the high 16 bits of the MAPI tag); 0x8000 and up are named properties.</param>
/// <param name="Type">The stored type.</param>
/// <param name="Value">
/// The value: <see cref="string"/>, <see cref="int"/>, <see cref="short"/>, <see cref="long"/>, <see cref="bool"/>,
/// <see cref="double"/>, <see cref="float"/>, <see cref="decimal"/>, <see cref="DateTime"/> (UTC), <see cref="Guid"/>,
/// <c>byte[]</c>, or an array of those for the multi-valued types; null when the stored value is empty.
/// </param>
/// <param name="Name">The property's name when known: a named property's string name, or the PidTag name <see cref="PstPropertyIds"/> knows.</param>
/// <param name="Named">For a named property, what the file's name-to-id map says of it.</param>
public readonly record struct PstProperty(ushort Id, PstPropertyType Type, object? Value, string? Name, PstNamedProperty? Named)
{
    /// <summary>The MAPI property tag: the id in the high 16 bits, the type in the low.</summary>
    public uint Tag => ((uint)Id << 16) | (ushort)Type;

    /// <summary>Whether the id is a named property's (0x8000 and up).</summary>
    public bool IsNamed => Id >= 0x8000;

    /// <inheritdoc/>
    public override string ToString()
    {
        var shown = Value switch
        {
            null => "(empty)",
            byte[] b => $"{b.Length} bytes",
            string s => s,
            Array a => $"{a.Length} values",
            _ => Value.ToString() ?? "",
        };
        return $"{Name ?? Named?.ToString() ?? $"0x{Id:X4}"} ({Type}) = {shown}";
    }
}

/// <summary>
/// An item's properties, read from the file on demand. Values are decoded by their stored type; 8-bit strings use the
/// item's own code page (PidTagMessageCodepage) or, failing that, the one the file was opened with.
/// </summary>
public sealed class PstProperties : IEnumerable<PstProperty>
{
    private readonly PstFile _file;
    private readonly IPropertySource _source;
    private readonly Lazy<int> _codePage;

    internal PstProperties(PstFile file, IPropertySource source)
    {
        _file = file;
        _source = source;
        _codePage = new Lazy<int>(() => RawInt32(PstPropertyIds.MessageCodepage) ?? file.AnsiCodePage);
    }

    /// <summary>The code page the item's 8-bit strings are decoded with.</summary>
    public int CodePage => _codePage.Value;

    /// <summary>The ids of the properties the item has.</summary>
    public IEnumerable<ushort> Ids => _source.Ids;

    /// <summary>Whether the item has the property.</summary>
    public bool Contains(ushort id) => _source.Contains(id);

    /// <summary>The property, decoded; null when the item has no such property.</summary>
    public PstProperty? Get(ushort id)
    {
        var raw = _source.Get(id);
        return raw is null ? null : Decode(raw.Value);
    }

    /// <summary>The property's value, or null.</summary>
    public object? this[ushort id] => Get(id)?.Value;

    /// <summary>The value when it is a string (PtypString or PtypString8), else null.</summary>
    public string? GetString(ushort id) => Get(id)?.Value as string;

    /// <summary>The value when it is an integer that fits 32 bits, else null.</summary>
    public int? GetInt32(ushort id) => Get(id)?.Value switch
    {
        int i => i,
        short s => s,
        long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
        _ => null,
    };

    /// <summary>The value when it is an integer, else null.</summary>
    public long? GetInt64(ushort id) => Get(id)?.Value switch
    {
        long l => l,
        int i => i,
        short s => s,
        _ => null,
    };

    /// <summary>The value when it is a boolean (or an integer, taken as non-zero), else null.</summary>
    public bool? GetBoolean(ushort id) => Get(id)?.Value switch
    {
        bool b => b,
        int i => i != 0,
        short s => s != 0,
        _ => null,
    };

    /// <summary>The value when it is a time (UTC), else null.</summary>
    public DateTime? GetDateTime(ushort id) => Get(id)?.Value as DateTime?;

    /// <summary>The value when it is bytes, else null.</summary>
    public byte[]? GetBinary(ushort id) => Get(id)?.Value as byte[];

    /// <summary>The value when it is a GUID, else null.</summary>
    public Guid? GetGuid(ushort id) => Get(id)?.Value as Guid?;

    /// <summary>The value when it is a number with a fraction (float, double, currency), else null.</summary>
    public double? GetDouble(ushort id) => Get(id)?.Value switch
    {
        double d => d,
        float f => f,
        decimal m => (double)m,
        _ => null,
    };

    /// <summary>Every property, in id order, decoded.</summary>
    public IEnumerator<PstProperty> GetEnumerator() => _source.All().OrderBy(r => r.Id).Select(Decode).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private int? RawInt32(ushort id)
    {
        var raw = _source.Get(id);
        return raw is null ? null : PropertyValues.Decode(raw.Value.Type, raw.Value.Bytes, 1252) as int?;
    }

    private PstProperty Decode(RawProperty raw)
    {
        var value = PropertyValues.Decode(raw.Type, raw.Bytes, CodePage);
        var named = raw.Id >= 0x8000 ? _file.NamedProperties.Get(raw.Id) : null;
        var name = named?.Name ?? PstPropertyIds.NameOf(raw.Id);
        return new PstProperty(raw.Id, (PstPropertyType)raw.Type, value, name, named);
    }
}
