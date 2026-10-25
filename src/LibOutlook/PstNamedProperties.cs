using System.Buffers.Binary;
using System.Collections;
using System.Text;
using LibOutlook.Ltp;

namespace LibOutlook;

/// <summary>A named property as the file's name-to-id map defines it ([MS-PST] 2.4.7): a property set and, in it, a name or a number.</summary>
/// <param name="PropertyId">The id the file uses for it, 0x8000 and up.</param>
/// <param name="PropertySet">The property set's GUID (PSETID_Address, PSETID_Appointment, PS_PUBLIC_STRINGS and so on).</param>
/// <param name="Name">The name, for a property named by a string.</param>
/// <param name="NumericId">The id within the set, for a property named by a number (PidLid values).</param>
public sealed record PstNamedProperty(ushort PropertyId, Guid PropertySet, string? Name, uint? NumericId)
{
    /// <summary>Whether the property is named by a string rather than a number.</summary>
    public bool IsString => Name is not null;

    /// <inheritdoc/>
    public override string ToString() => Name ?? (NumericId is { } n ? $"0x{n:X4}" : $"0x{PropertyId:X4}");
}

/// <summary>The file's name-to-id map: which named property each id from 0x8000 up stands for.</summary>
public sealed class PstNamedProperties : IReadOnlyCollection<PstNamedProperty>
{
    /// <summary>PS_MAPI.</summary>
    public static readonly Guid MapiPropertySet = new("00020328-0000-0000-C000-000000000046");

    /// <summary>PS_PUBLIC_STRINGS.</summary>
    public static readonly Guid PublicStringsPropertySet = new("00020329-0000-0000-C000-000000000046");

    /// <summary>PSETID_Appointment: an appointment's start and end, its recurrence and time zones ([MS-OXOCAL]).</summary>
    public static readonly Guid AppointmentPropertySet = new("00062002-0000-0000-C000-000000000046");

    private readonly Dictionary<ushort, PstNamedProperty> _byId = [];

    internal PstNamedProperties(PstFile file)
    {
        var node = file.FindNode(NodeId.NameToIdMap);
        if (node is null)
        {
            return;
        }

        var map = new PropertyContext(file.Context(node.Value));
        var guids = map.Get(0x0002)?.Bytes ?? [];
        var entries = map.Get(0x0003)?.Bytes ?? [];
        var strings = map.Get(0x0004)?.Bytes ?? [];
        for (var at = 0; at + 8 <= entries.Length; at += 8)
        {
            var value = BinaryPrimitives.ReadUInt32LittleEndian(entries.AsSpan(at));
            var kind = BinaryPrimitives.ReadUInt16LittleEndian(entries.AsSpan(at + 4));
            var index = BinaryPrimitives.ReadUInt16LittleEndian(entries.AsSpan(at + 6));
            var propertyId = (ushort)(0x8000 + index);
            var set = (kind >> 1) switch
            {
                0 => Guid.Empty,
                1 => MapiPropertySet,
                2 => PublicStringsPropertySet,
                var n => GuidAt(guids, n - 3),
            };
            var isString = (kind & 1) != 0;
            _byId[propertyId] = new PstNamedProperty(propertyId, set, isString ? StringAt(strings, value) : null, isString ? null : value);
        }
    }

    /// <inheritdoc/>
    public int Count => _byId.Count;

    /// <summary>The named property behind an id, or null when the id is below 0x8000 or the map has no such entry.</summary>
    public PstNamedProperty? Get(ushort propertyId) => _byId.TryGetValue(propertyId, out var p) ? p : null;

    /// <summary>The property named by a string in a set, or null when the file has none.</summary>
    public PstNamedProperty? Find(Guid propertySet, string name)
        => _byId.Values.FirstOrDefault(p => p.PropertySet == propertySet && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The property named by a number in a set (a PidLid), or null when the file has none.</summary>
    public PstNamedProperty? Find(Guid propertySet, uint numericId)
        => _byId.Values.FirstOrDefault(p => p.PropertySet == propertySet && p.NumericId == numericId);

    /// <inheritdoc/>
    public IEnumerator<PstNamedProperty> GetEnumerator() => _byId.Values.OrderBy(p => p.PropertyId).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private static Guid GuidAt(byte[] guids, int index)
        => index >= 0 && (index + 1) * 16 <= guids.Length ? new Guid(guids.AsSpan(index * 16, 16)) : Guid.Empty;

    /// <summary>The string at an offset of the string stream: a byte count then the bytes, UTF-16 as a rule, plain ASCII in a few files.</summary>
    private static string? StringAt(byte[] strings, uint offset)
    {
        if (offset + 4 > (uint)strings.Length)
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(strings.AsSpan((int)offset));
        if (length < 0 || offset + 4 + (uint)length > (uint)strings.Length)
        {
            return null;
        }

        var bytes = strings.AsSpan((int)offset + 4, length);
        var ascii = length % 2 == 1 || (bytes.IndexOf((byte)0) < 0 && !bytes.ContainsAnyExceptInRange((byte)0x20, (byte)0x7E));
        var s = ascii ? Encoding.Latin1.GetString(bytes) : Encoding.Unicode.GetString(bytes);
        return s.TrimEnd('\0');
    }
}
